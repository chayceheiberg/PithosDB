using Pithos.Core.Compaction;
using Pithos.Core.Core;
using Pithos.Core.Storage;

namespace Pithos.Core;

public sealed class PithosDb : IDisposable
{
    private const long MemTableSizeThreshold = 4 * 1024 * 1024; // 4 MB

    private readonly string _directory;
    private readonly LeveledCompactor _compactor;
    private readonly List<List<string>> _levels = [];
    private readonly Lock _writeLock = new();

    private MemTable _memTable = new();
    private WriteAheadLog _wal;

    public PithosDb(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _compactor = new LeveledCompactor(directory);
        _wal = new WriteAheadLog(Path.Combine(directory, "wal.log"));
        RecoverFromWal();
        RecoverSSTables();
    }

    public void Put(byte[] key, byte[] value)
    {
        lock (_writeLock)
        {
            _wal.AppendPut(key, value);
            _memTable.Put(key, value);
            MaybeFlushMemTable();
        }
    }

    public void Delete(byte[] key)
    {
        lock (_writeLock)
        {
            _wal.AppendDelete(key);
            _memTable.Delete(key);
            MaybeFlushMemTable();
        }
    }

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

    public void Dispose() => _wal.Dispose();
}
