using Pithos.Core.Core;

namespace Pithos.Core.Storage;

/// <summary>
/// Reads an immutable SSTable file written by <see cref="SSTableWriter"/>.
/// On open, the sparse index and bloom filter are loaded into memory; data
/// blocks remain on disk and are read on demand. Each instance holds an open
/// file handle — dispose when done.
/// </summary>
public sealed class SSTableReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly List<(byte[] firstKey, long offset)> _index;
    private readonly BloomFilter _bloom;
    private readonly long _bloomOffset;

    /// <summary>Absolute path to the SSTable file.</summary>
    public string Path { get; }

    /// <summary>
    /// Opens the SSTable at <paramref name="path"/> and loads its index and
    /// bloom filter into memory.
    /// </summary>
    public SSTableReader(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream);
        (_index, _bloom, _bloomOffset) = ReadMetadata();
    }

    /// <summary>
    /// Looks up <paramref name="key"/> in this SSTable. The bloom filter is
    /// consulted first; a definite miss returns <see langword="false"/> without
    /// any block I/O. Returns <see langword="true"/> for tombstones (with a
    /// <see langword="null"/> <paramref name="value"/>), allowing callers to
    /// distinguish "found a tombstone" from "not present".
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">
    /// The stored value on success, <see langword="null"/> for a tombstone,
    /// or <see langword="null"/> when the method returns <see langword="false"/>.
    /// </param>
    public bool TryGet(byte[] key, out byte[]? value)
    {
        value = null;
        if (!_bloom.MightContain(key)) return false;

        var blockOffset = FindBlockOffset(key);
        if (blockOffset < 0) return false;

        _stream.Seek(blockOffset, SeekOrigin.Begin);
        int count = _reader.ReadInt32();

        for (int i = 0; i < count; i++)
        {
            var keyLen = _reader.ReadInt32();
            var entryKey = _reader.ReadBytes(keyLen);
            var isTombstone = _reader.ReadBoolean();
            byte[]? entryValue = null;
            if (!isTombstone)
            {
                var valLen = _reader.ReadInt32();
                entryValue = _reader.ReadBytes(valLen);
            }

            int cmp = ByteArrayComparer.Instance.Compare(entryKey, key);
            if (cmp == 0) { value = entryValue; return true; }
            if (cmp > 0) return false;
        }
        return false;
    }

    /// <summary>
    /// Streams all entries in byte-lexicographic key order, including tombstones.
    /// Used by <see cref="Compaction.LeveledCompactor"/> during compaction.
    /// </summary>
    public IEnumerable<KeyValuePair<byte[], byte[]?>> ReadAllEntries()
    {
        if (_index.Count == 0) yield break;

        _stream.Seek(_index[0].offset, SeekOrigin.Begin);

        while (_stream.Position < _bloomOffset)
        {
            int count = _reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var keyLen = _reader.ReadInt32();
                var key = _reader.ReadBytes(keyLen);
                var isTombstone = _reader.ReadBoolean();
                byte[]? value = null;
                if (!isTombstone)
                {
                    var valLen = _reader.ReadInt32();
                    value = _reader.ReadBytes(valLen);
                }
                yield return new KeyValuePair<byte[], byte[]?>(key, value);
            }
        }
    }

    /// <summary>
    /// Binary-searches the sparse index for the last block whose first key is
    /// ≤ <paramref name="key"/>. Returns -1 if the key precedes the first block.
    /// </summary>
    private long FindBlockOffset(byte[] key)
    {
        int lo = 0, hi = _index.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int cmp = ByteArrayComparer.Instance.Compare(_index[mid].firstKey, key);
            if (cmp <= 0) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result < 0 ? -1 : _index[result].offset;
    }

    /// <summary>
    /// Reads the 16-byte footer to locate the bloom filter and index sections,
    /// then deserializes both into memory.
    /// </summary>
    private (List<(byte[] firstKey, long offset)> index, BloomFilter bloom, long bloomOffset) ReadMetadata()
    {
        // Footer layout (last 16 bytes): [bloomOffset (8)] [indexOffset (8)]
        _stream.Seek(-16, SeekOrigin.End);
        long bloomOffset = _reader.ReadInt64();
        long indexOffset = _reader.ReadInt64();

        _stream.Seek(bloomOffset, SeekOrigin.Begin);
        int hashCount = _reader.ReadInt32();
        int bitCount = _reader.ReadInt32();
        var bits = new bool[bitCount];
        for (int i = 0; i < bitCount; i++)
            bits[i] = _reader.ReadBoolean();
        var bloom = new BloomFilter(bits, hashCount);

        _stream.Seek(indexOffset, SeekOrigin.Begin);
        int count = _reader.ReadInt32();
        var index = new List<(byte[], long)>(count);
        for (int i = 0; i < count; i++)
        {
            var keyLen = _reader.ReadInt32();
            var key = _reader.ReadBytes(keyLen);
            var offset = _reader.ReadInt64();
            index.Add((key, offset));
        }

        return (index, bloom, bloomOffset);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
