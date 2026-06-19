using Pithos.Core.Compaction;
using Pithos.Core.Core;
using Pithos.Core.Storage;

namespace Pithos.Core;

/// <summary>
/// Embedded key-value store backed by an LSM-tree. Writes are buffered in a
/// <see cref="MemTable"/>, durably recorded in a <see cref="WriteAheadLog"/>,
/// and periodically flushed to immutable <see cref="SSTableWriter">SSTables</see>
/// on disk. Writes are thread-safe; concurrent reads are safe without locking.
/// </summary>
public sealed class PithosDb : IDisposable
{
    private const long MemTableSizeThreshold = 4 * 1024 * 1024; // 4 MB

    private readonly string _directory;
    private readonly LeveledCompactor _compactor;
    private readonly List<List<string>> _levels = [];
    private readonly Lock _writeLock = new();

    private MemTable _memTable = new();
    private WriteAheadLog _wal;

    /// <summary>
    /// Opens or creates a database in <paramref name="directory"/>. Any unflushed
    /// WAL entries are replayed and existing SSTable files are recovered into the
    /// level structure before the instance is returned.
    /// </summary>
    /// <param name="directory">
    /// Path to the database directory. Created if it does not exist.
    /// </param>
    public PithosDb(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _compactor = new LeveledCompactor(directory);
        _wal = new WriteAheadLog(Path.Combine(directory, "wal.log"));
        RecoverFromWal();
        RecoverSSTables();
    }

    /// <summary>
    /// Inserts or updates <paramref name="key"/> with <paramref name="value"/>.
    /// The write is appended to the WAL before being applied to the MemTable.
    /// A MemTable flush (and possible compaction) is triggered when the buffer
    /// exceeds 4 MB.
    /// </summary>
    public void Put(byte[] key, byte[] value)
    {
        lock (_writeLock)
        {
            _wal.AppendPut(key, value);
            _memTable.Put(key, value);
            MaybeFlushMemTable();
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
        lock (_writeLock)
        {
            _wal.AppendDelete(key);
            _memTable.Delete(key);
            MaybeFlushMemTable();
        }
    }

    /// <summary>
    /// Looks up <paramref name="key"/>, searching the MemTable first, then each
    /// SSTable level from newest to oldest. Returns <see langword="false"/> if
    /// the key does not exist or has been deleted.
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
        if (_memTable.TryGet(key, out value))
            return value is not null;

        foreach (var level in _levels)
        {
            foreach (var sstPath in Enumerable.Reverse(level))
            {
                using var reader = new SSTableReader(sstPath);
                if (reader.TryGet(key, out value))
                    return value is not null;
            }
        }

        value = null;
        return false;
    }

    private void MaybeFlushMemTable()
    {
        if (_memTable.SizeBytes < MemTableSizeThreshold) return;

        if (_levels.Count == 0) _levels.Add([]);

        string sstPath = Path.Combine(_directory, $"L0_{Guid.NewGuid():N}.sst");
        SSTableWriter.Write(sstPath, _memTable.GetSortedEntries());
        _levels[0].Add(sstPath);
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
        }
    }

    /// <summary>Flushes and closes the WAL.</summary>
    public void Dispose() => _wal.Dispose();
}
