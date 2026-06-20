using PithosDB.Core.Core;
using PithosDB.Core.Storage;

namespace PithosDB.Core.Compaction;

/// <summary>
/// Implements leveled compaction for the LSM-tree. The engine uses 7 levels
/// whose file-count limits grow by 10× per level (L0 = 10, L1 = 100, …).
/// When a level reaches its limit, all its SSTables are merged into a single
/// SSTable at the next level, deduplicating keys so that newer writes win.
/// </summary>
public sealed class LeveledCompactor
{
    private readonly string _directory;
    private readonly PithosOptions _options;
    private readonly int[] _levelSizeLimits;
    private readonly Dictionary<string, SSTableReader> _readerCache;
    private readonly IBlockCache? _blockCache;
    private readonly Manifest? _manifest;
    private readonly ICompactionFilter? _filter;

    /// <summary>
    /// Creates a compactor for the database at <paramref name="directory"/> using
    /// the provided <paramref name="options"/>.
    /// </summary>
    /// <param name="directory">Database directory where SSTable files are written.</param>
    /// <param name="options">Options governing level count, size limits, and bloom filter FPR.</param>
    /// <param name="readerCache">
    /// Shared SSTableReader cache owned by the database. The compactor evicts
    /// source-file entries before deleting them and registers the merged output
    /// so reads can immediately use the cached reader.
    /// </param>
    /// <param name="blockCache">Optional shared block cache.</param>
    /// <param name="manifest">
    /// Optional manifest. When provided, the manifest is updated after each
    /// compaction step before source files are deleted, ensuring crash-safe recovery.
    /// </param>
    public LeveledCompactor(string directory, PithosOptions options, Dictionary<string, SSTableReader> readerCache,
        IBlockCache? blockCache = null, Manifest? manifest = null)
    {
        _directory = directory;
        _options = options;
        _readerCache = readerCache;
        _blockCache = blockCache;
        _manifest = manifest;
        _filter = options.CompactionFilter;
        _levelSizeLimits = new int[options.LevelCount];
        int size = options.LevelZeroFileCountLimit;
        for (int i = 0; i < options.LevelCount; i++) { _levelSizeLimits[i] = size; size *= options.LevelSizeMultiplier; }
    }

    /// <summary>
    /// Checks each level and triggers a compaction cascade for any level that has
    /// reached its file-count limit. Called after every MemTable flush.
    /// </summary>
    public void CompactIfNeeded(List<List<string>> levels)
    {
        // Iterate all compactable levels (0 to LevelCount-2). Stop early if the
        // level doesn't exist yet — levels are created sequentially so higher
        // levels can't exist if a lower one is absent. Compact() creates the
        // destination level on demand, so we don't need it to exist up front.
        for (int level = 0; level < _levelSizeLimits.Length - 1; level++)
        {
            if (level >= levels.Count) break;
            if (levels[level].Count >= _levelSizeLimits[level])
                Compact(levels, level);
        }
    }

    /// <summary>
    /// Merges all SSTables at <paramref name="level"/> into a single SSTable at
    /// <c>level + 1</c>, then deletes the source files.
    /// <para>
    /// Crash-safe ordering: the manifest is written after the merged file is
    /// created but before source files are deleted. A crash between those two
    /// points leaves the manifest in a consistent state; orphaned files are
    /// cleaned up on the next open.
    /// </para>
    /// </summary>
    private void Compact(List<List<string>> levels, int level)
    {
        while (levels.Count <= level + 1)
            levels.Add([]);

        var sources = levels[level].ToList();
        levels[level].Clear();

        var readers = sources.Select(p => new SSTableReader(p)).ToList();
        string? outPath = null;
        try
        {
            var merged = MergeEntries(readers, _options.EnableTtl, _filter);
            outPath = System.IO.Path.Combine(_directory, $"L{level + 1}_{Guid.NewGuid():N}.sst");
            SSTableWriter.Write(outPath, merged, _options.BloomFilterFalsePositiveRate, _options.Compression);
            levels[level + 1].Add(outPath);
            _readerCache[outPath] = new SSTableReader(outPath, _blockCache);

            // Write manifest with the new file added and sources removed, BEFORE
            // deleting the source files. If we crash here the manifest is consistent;
            // the stale source files become orphans and are removed on next open.
            _manifest?.Write(levels);

            // Release all file handles on source files before deleting them.
            foreach (var r in readers) r.Dispose();
            readers.Clear();

            foreach (var p in sources)
            {
                _blockCache?.EvictFile(p);
                if (_readerCache.TryGetValue(p, out var cached))
                {
                    _readerCache.Remove(p);
                    cached.Dispose();
                }
                File.Delete(p);
            }
        }
        catch
        {
            // Roll back in-memory state. The outPath file (if created) is an orphan
            // that will be removed on next open via orphan cleanup.
            levels[level].AddRange(sources);
            if (outPath is not null)
            {
                levels[level + 1].Remove(outPath);
                if (_readerCache.TryGetValue(outPath, out var r))
                {
                    _readerCache.Remove(outPath);
                    r.Dispose();
                }
            }
            throw;
        }
        finally
        {
            foreach (var r in readers) r.Dispose(); // no-op if already disposed above
        }
    }

    /// <summary>
    /// Performs a k-way merge over <paramref name="readers"/> using a
    /// <see cref="PriorityQueue{TElement,TPriority}"/> ordered by
    /// <c>(key asc, readerIndex desc)</c>. For duplicate keys across readers,
    /// the entry from the highest-indexed reader (i.e. the newest SSTable) is
    /// emitted and all older copies are discarded.
    /// </summary>
    private static IEnumerable<KeyValuePair<byte[], byte[]?>> MergeEntries(
        List<SSTableReader> readers, bool enableTtl, ICompactionFilter? filter)
    {
        var comparer = ByteArrayComparer.Instance;
        var enumerators = readers.Select(r => r.ReadAllEntries().GetEnumerator()).ToList();

        // Priority: key asc, then negated readerIndex asc (so higher index = newer = dequeued first on ties)
        var pq = new PriorityQueue<(byte[] key, byte[]? value, int readerIdx), (byte[] key, int negIdx)>(
            Comparer<(byte[] key, int negIdx)>.Create((a, b) =>
            {
                int c = comparer.Compare(a.key, b.key);
                return c != 0 ? c : a.negIdx.CompareTo(b.negIdx);
            }));

        for (int i = 0; i < enumerators.Count; i++)
        {
            if (enumerators[i].MoveNext())
            {
                var kv = enumerators[i].Current;
                pq.Enqueue((kv.Key, kv.Value, i), (kv.Key, -i));
            }
        }

        byte[]? lastKey = null;
        while (pq.Count > 0)
        {
            var (key, value, idx) = pq.Dequeue();

            if (lastKey is null || comparer.Compare(lastKey, key) != 0)
            {
                lastKey = key;

                if (value is null)
                {
                    // Tombstone — always pass through so it shadows older copies.
                    yield return new KeyValuePair<byte[], byte[]?>(key, null);
                }
                else
                {
                    // Decode for TTL check and filter; re-emit original encoded bytes
                    // so the TTL header is preserved for future reads and compactions.
                    byte[] userValue = value;
                    bool keep = true;

                    if (enableTtl)
                    {
                        var (decoded, dropped) = ValueCodec.DecodeForCompaction(value);
                        if (dropped) keep = false;
                        else userValue = decoded!;
                    }

                    if (keep && (filter is null || filter.ShouldKeep(key, userValue)))
                        yield return new KeyValuePair<byte[], byte[]?>(key, value);
                }
            }

            if (enumerators[idx].MoveNext())
            {
                var kv = enumerators[idx].Current;
                pq.Enqueue((kv.Key, kv.Value, idx), (kv.Key, -idx));
            }
        }

        foreach (var e in enumerators) e.Dispose();
    }
}
