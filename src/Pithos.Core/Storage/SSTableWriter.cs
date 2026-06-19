using Pithos.Core.Core;

namespace Pithos.Core.Storage;

/// <summary>
/// Writes an immutable, sorted SSTable file from a sequence of key-value entries.
/// <para>
/// File layout:
/// <code>
/// [ data blocks (≤4 KB each) ]
/// [ bloom filter section     ]  ← hashCount (int32), bitCount (int32), bits (bool[])
/// [ sparse index section     ]  ← entry count (int32), (keyLen, key, blockOffset)…
/// [ footer (16 bytes)        ]  ← bloomOffset (int64), indexOffset (int64)
/// </code>
/// </para>
/// </summary>
public sealed class SSTableWriter
{
    private const int BlockSize = 4096;

    /// <summary>
    /// Writes <paramref name="entries"/> to a new SSTable file at <paramref name="path"/>.
    /// Entries must be supplied in byte-lexicographic key order. The file must not
    /// already exist.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="entries">
    /// Sorted key-value pairs to write. A <see langword="null"/> value encodes a tombstone.
    /// </param>
    public static void Write(string path, IEnumerable<KeyValuePair<byte[], byte[]?>> entries)
    {
        var entryList = entries.ToList();

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        var bloom = new BloomFilter(Math.Max(1, entryList.Count));
        foreach (var (key, _) in entryList)
            bloom.Add(key);

        var index = new List<(byte[] firstKey, long offset)>();
        var blockBuffer = new List<KeyValuePair<byte[], byte[]?>>();
        long blockStart = 0;

        foreach (var entry in entryList)
        {
            blockBuffer.Add(entry);
            if (EstimateBlockSize(blockBuffer) >= BlockSize)
                FlushBlock(writer, blockBuffer, index, ref blockStart);
        }

        if (blockBuffer.Count > 0)
            FlushBlock(writer, blockBuffer, index, ref blockStart);

        // Bloom filter section
        long bloomOffset = stream.Position;
        var (bits, hashCount) = bloom.Serialize();
        writer.Write(hashCount);
        writer.Write(bits.Length);
        foreach (var bit in bits)
            writer.Write(bit);

        // Index section
        long indexOffset = stream.Position;
        writer.Write(index.Count);
        foreach (var (firstKey, offset) in index)
        {
            writer.Write(firstKey.Length);
            writer.Write(firstKey);
            writer.Write(offset);
        }

        // Footer: bloomOffset then indexOffset (16 bytes total)
        writer.Write(bloomOffset);
        writer.Write(indexOffset);
    }

    private static void FlushBlock(
        BinaryWriter writer,
        List<KeyValuePair<byte[], byte[]?>> entries,
        List<(byte[] firstKey, long offset)> index,
        ref long blockStart)
    {
        index.Add((entries[0].Key, blockStart));
        writer.Write(entries.Count);
        foreach (var (key, value) in entries)
        {
            writer.Write(key.Length);
            writer.Write(key);
            bool isTombstone = value is null;
            writer.Write(isTombstone);
            if (!isTombstone)
            {
                writer.Write(value!.Length);
                writer.Write(value);
            }
        }
        blockStart = writer.BaseStream.Position;
        entries.Clear();
    }

    private static int EstimateBlockSize(List<KeyValuePair<byte[], byte[]?>> entries)
    {
        int size = 0;
        foreach (var (key, value) in entries)
            size += key.Length + (value?.Length ?? 0) + 9;
        return size;
    }
}
