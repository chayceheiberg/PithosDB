using Pithos.Core.Core;

namespace Pithos.Core.Storage;

public sealed class SSTableReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly List<(byte[] firstKey, long offset)> _index;
    private readonly BloomFilter _bloom;
    private readonly long _bloomOffset;

    public string Path { get; }

    public SSTableReader(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream);
        (_index, _bloom, _bloomOffset) = ReadMetadata();
    }

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

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
