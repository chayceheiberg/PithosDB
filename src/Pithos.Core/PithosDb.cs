using Pithos.Core.Compaction;
using Pithos.Core.Core;
using Pithos.Core.Storage;

namespace Pithos.Core;

/// <summary>
/// Embedded key-value store backed by an LSM-tree. Writes are buffered in a
/// <see cref="MemTable"/>, durably recorded in a <see cref="WriteAheadLog"/>,
/// and periodically flushed to immutable <see cref="SSTableWriter">SSTables</see>
/// on disk. All public operations are thread-safe: concurrent reads proceed in
/// parallel; a write briefly blocks all readers while the MemTable and level
/// list are updated.
/// </summary>
public sealed class PithosDb : IDisposable
{
    private readonly string _directory;
    private readonly PithosOptions _options;
    private readonly LeveledCompactor _compactor;
    private readonly List<List<string>> _levels = [];
    private readonly Dictionary<string, SSTableReader> _readerCache = new();
    private readonly IBlockCache? _blockCache;
    private readonly ReaderWriterLockSlim _lock = new();

    private MemTable _memTable = new();
    private WriteAheadLog _wal;

    /// <summary>
    /// Opens or creates a database in <paramref name="directory"/>. Any unflushed
    /// WAL entries are replayed and existing SSTable files are recovered into the
    /// level structure before the instance is returned.
    /// </summary>
    /// <param name="directory">Path to the database directory. Created if it does not exist.</param>
    /// <param name="options">
    /// Tuning options. Pass <see langword="null"/> or omit to use <see cref="PithosOptions.Default"/>.
    /// </param>
    public PithosDb(string directory, PithosOptions? options = null)
    {
        _options = options ?? PithosOptions.Default;
        _options.Validate();
        _directory = directory;
        Directory.CreateDirectory(directory);
        _blockCache = _options.BlockCacheSizeBytes > 0
            ? _options.BlockCacheKind == BlockCacheKind.S3Fifo
                ? new S3FifoBlockCache(_options.BlockCacheSizeBytes)
                : new LruBlockCache(_options.BlockCacheSizeBytes)
            : null;
        _compactor = new LeveledCompactor(directory, _options, _readerCache, _blockCache);
        _wal = new WriteAheadLog(Path.Combine(directory, "wal.log"));
        RecoverFromWal();
        RecoverSSTables();
    }

    /// <summary>
    /// Inserts or updates <paramref name="key"/> with <paramref name="value"/>.
    /// The write is appended to the WAL before being applied to the MemTable.
    /// A MemTable flush (and possible compaction) is triggered when the buffer
    /// exceeds <see cref="PithosOptions.MemTableSizeThreshold"/>.
    /// </summary>
    public void Put(byte[] key, byte[] value)
    {
        _lock.EnterWriteLock();
        try
        {
            _wal.AppendPut(key, value);
            _memTable.Put(key, value);
            MaybeFlushMemTable();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Applies all operations in <paramref name="batch"/> atomically. The entire
    /// batch is written to the WAL as a single CRC-guarded record before any
    /// MemTable mutation occurs, so either every operation survives a crash or none do.
    /// </summary>
    public void Write(WriteBatch batch)
    {
        if (batch.Operations.Count == 0) return;

        _lock.EnterWriteLock();
        try
        {
            _wal.AppendBatch(batch.Operations);
            foreach (var (type, key, value) in batch.Operations)
            {
                if (type == WalEntryType.Put) _memTable.Put(key, value!);
                else _memTable.Delete(key);
            }
            MaybeFlushMemTable();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Deletes <paramref name="key"/> by writing a tombstone. Subsequent
    /// <see cref="TryGet"/> calls for this key return <see langword="false"/>
    /// until a new value is written. The tombstone is physically removed during
    /// compaction.
    /// </summary>
    public void Delete(byte[] key)
    {
        _lock.EnterWriteLock();
        try
        {
            _wal.AppendDelete(key);
            _memTable.Delete(key);
            MaybeFlushMemTable();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Looks up <paramref name="key"/>, searching the MemTable first, then each
    /// SSTable level from newest to oldest. Returns <see langword="false"/> if
    /// the key does not exist or has been deleted. Multiple threads may call
    /// <see cref="TryGet"/> concurrently.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">
    /// Set to the stored value on success, or <see langword="null"/> on failure.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the key exists and has not been deleted;
    /// <see langword="false"/> otherwise.
    /// </returns>
    public bool TryGet(byte[] key, out byte[]? value)
    {
        _lock.EnterReadLock();
        try
        {
            if (_memTable.TryGet(key, out value))
                return value is not null;

            foreach (var level in _levels)
            {
                foreach (var sstPath in Enumerable.Reverse(level))
                {
                    if (_readerCache[sstPath].TryGet(key, out value))
                        return value is not null;
                }
            }

            value = null;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns all live key-value pairs whose keys fall within the inclusive range
    /// [<paramref name="from"/>, <paramref name="to"/>], in sorted order. Omit either
    /// bound for an open-ended scan; omit both for a full scan.
    /// </summary>
    public IEnumerable<(byte[] key, byte[] value)> Scan(byte[]? from = null, byte[]? to = null)
    {
        _lock.EnterReadLock();
        try
        {
            return CollectScan(from, to);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private List<(byte[] key, byte[] value)> CollectScan(byte[]? from, byte[]? to)
    {
        var comparer = ByteArrayComparer.Instance;

        // Build source list ordered newest → oldest: MemTable first, then each
        // level's files in reverse (most recently flushed file = newest within level).
        var sources = new List<IEnumerable<KeyValuePair<byte[], byte[]?>>>
        {
            _memTable.GetSortedEntries()
        };
        foreach (var level in _levels)
            foreach (var path in Enumerable.Reverse(level))
                sources.Add(_readerCache[path].ReadAllEntries());

        // k-way merge across all sorted sources. On key collision, the source with
        // the lower index wins (lower index = newer). Priority: key asc, then idx asc.
        var pq = new PriorityQueue<(byte[] key, byte[]? value, int src), (byte[], int)>(
            Comparer<(byte[], int)>.Create((a, b) =>
            {
                int c = comparer.Compare(a.Item1, b.Item1);
                return c != 0 ? c : a.Item2.CompareTo(b.Item2);
            }));

        var enumerators = sources.Select(s => s.GetEnumerator()).ToList();
        for (int i = 0; i < enumerators.Count; i++)
        {
            if (enumerators[i].MoveNext())
            {
                var kv = enumerators[i].Current;
                pq.Enqueue((kv.Key, kv.Value, i), (kv.Key, i));
            }
        }

        var results = new List<(byte[] key, byte[] value)>();
        byte[]? lastKey = null;

        while (pq.Count > 0)
        {
            var (key, value, idx) = pq.Dequeue();

            bool isDuplicate = lastKey is not null && comparer.Compare(lastKey, key) == 0;
            lastKey = key;

            if (enumerators[idx].MoveNext())
            {
                var kv = enumerators[idx].Current;
                pq.Enqueue((kv.Key, kv.Value, idx), (kv.Key, idx));
            }

            if (isDuplicate) continue;  // older source shadowed by a newer one
            if (value is null) continue; // tombstone

            if (from is not null && comparer.Compare(key, from) < 0) continue;
            if (to   is not null && comparer.Compare(key, to)   > 0) break; // sorted — done

            results.Add((key, value));
        }

        foreach (var e in enumerators) e.Dispose();
        return results;
    }

    private void MaybeFlushMemTable()
    {
        if (_memTable.SizeBytes < _options.MemTableSizeThreshold) return;

        if (_levels.Count == 0) _levels.Add([]);

        string sstPath = Path.Combine(_directory, $"L0_{Guid.NewGuid():N}.sst");
        SSTableWriter.Write(sstPath, _memTable.GetSortedEntries(), _options.BloomFilterFalsePositiveRate, _options.Compression);
        _levels[0].Add(sstPath);
        _readerCache[sstPath] = new SSTableReader(sstPath, _blockCache);
        _memTable.Clear();

        _wal.Dispose();
        File.Delete(Path.Combine(_directory, "wal.log"));
        _wal = new WriteAheadLog(Path.Combine(_directory, "wal.log"));

        _compactor.CompactIfNeeded(_levels);
    }

    private void RecoverFromWal()
    {
        foreach (var (type, key, value) in WriteAheadLog.Replay(Path.Combine(_directory, "wal.log")))
        {
            if (type == WalEntryType.Put) _memTable.Put(key, value!);
            else _memTable.Delete(key);
        }
    }

    private void RecoverSSTables()
    {
        foreach (var path in Directory.GetFiles(_directory, "*.sst").OrderBy(File.GetCreationTimeUtc))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var sep = name.IndexOf('_');
            if (sep < 2 || !name.StartsWith('L')) continue;
            if (!int.TryParse(name[1..sep], out int level)) continue;

            while (_levels.Count <= level) _levels.Add([]);
            _levels[level].Add(path);
            _readerCache[path] = new SSTableReader(path, _blockCache);
        }
    }

    /// <summary>Flushes and closes the WAL, disposes all cached SSTable readers, then disposes the lock.</summary>
    public void Dispose()
    {
        _wal.Dispose();
        foreach (var reader in _readerCache.Values)
            reader.Dispose();
        _readerCache.Clear();
        _lock.Dispose();
    }
}
