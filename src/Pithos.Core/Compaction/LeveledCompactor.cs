using Pithos.Core.Core;
using Pithos.Core.Storage;

namespace Pithos.Core.Compaction;

/// <summary>
/// Implements leveled compaction for the LSM-tree. The engine uses 7 levels
/// whose file-count limits grow by 10× per level (L0 = 10, L1 = 100, …).
/// When a level reaches its limit, all its SSTables are merged into a single
/// SSTable at the next level, deduplicating keys so that newer writes win.
/// </summary>
public sealed class LeveledCompactor
{
    private readonly string _directory;
    private readonly int[] _levelSizeLimits;

    /// <summary>
    /// Creates a compactor for the database at <paramref name="directory"/>.
    /// </summary>
    /// <param name="directory">Database directory where SSTable files are written.</param>
    /// <param name="levels">Total number of levels to manage (default 7).</param>
    public LeveledCompactor(string directory, int levels = 7)
    {
        _directory = directory;
        _levelSizeLimits = new int[levels];
        int size = 10;
        for (int i = 0; i < levels; i++) { _levelSizeLimits[i] = size; size *= 10; }
    }

    /// <summary>
    /// Checks each level and triggers a compaction cascade for any level that has
    /// reached its file-count limit. Called after every MemTable flush.
    /// </summary>
    public void CompactIfNeeded(List<List<string>> levels)
    {
        for (int level = 0; level < levels.Count - 1; level++)
        {
            if (levels[level].Count >= _levelSizeLimits[level])
                Compact(levels, level);
        }
    }

    /// <summary>
    /// Merges all SSTables at <paramref name="level"/> into a single SSTable at
    /// <c>level + 1</c>, then deletes the source files. The output file is placed
    /// in <c>_directory</c> with a name like <c>L{n+1}_{guid}.sst</c>.
    /// </summary>
    private void Compact(List<List<string>> levels, int level)
    {
        while (levels.Count <= level + 1)
            levels.Add([]);

        var sources = levels[level].ToList();
        levels[level].Clear();

        var readers = sources.Select(p => new SSTableReader(p)).ToList();
        try
        {
            var merged = MergeEntries(readers);
            string outPath = System.IO.Path.Combine(_directory, $"L{level + 1}_{Guid.NewGuid():N}.sst");
            SSTableWriter.Write(outPath, merged);
            levels[level + 1].Add(outPath);
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
            foreach (var p in sources) File.Delete(p);
        }
    }

    /// <summary>
    /// Performs a k-way merge over <paramref name="readers"/> using a
    /// <see cref="PriorityQueue{TElement,TPriority}"/> ordered by
    /// <c>(key asc, readerIndex desc)</c>. For duplicate keys across readers,
    /// the entry from the highest-indexed reader (i.e. the newest SSTable) is
    /// emitted and all older copies are discarded.
    /// </summary>
    private static IEnumerable<KeyValuePair<byte[], byte[]?>> MergeEntries(List<SSTableReader> readers)
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
                yield return new KeyValuePair<byte[], byte[]?>(key, value);
                lastKey = key;
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
