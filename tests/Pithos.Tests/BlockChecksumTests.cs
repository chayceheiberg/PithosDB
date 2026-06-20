using Pithos.Core.Storage;

namespace Pithos.Tests;

public class BlockChecksumTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public BlockChecksumTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ── Round-trip correctness ─────────────────────────────────────────────

    [Fact]
    public void WriteAndRead_SingleEntry_RoundTrips()
    {
        var path = Path.Combine(_dir, "t.sst");
        SSTableWriter.Write(path, [new KeyValuePair<byte[], byte[]?>(Bytes("key"), Bytes("val"))]);

        using var reader = new SSTableReader(path);
        Assert.True(reader.TryGet(Bytes("key"), out var v));
        Assert.Equal(Bytes("val"), v);
    }

    [Fact]
    public void WriteAndRead_MultiBlock_AllValuesCorrect()
    {
        var path = Path.Combine(_dir, "t.sst");
        // 100 entries with long enough values to span multiple 4 KB blocks.
        var entries = Enumerable.Range(0, 100)
            .Select(i => new KeyValuePair<byte[], byte[]?>(
                Bytes($"key-{i:D4}"),
                Bytes(new string('x', 60) + i)))
            .OrderBy(e => e.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ToList();

        SSTableWriter.Write(path, entries);

        using var reader = new SSTableReader(path);
        foreach (var (key, expected) in entries)
        {
            Assert.True(reader.TryGet(key, out var v), $"key {System.Text.Encoding.UTF8.GetString(key)} missing");
            Assert.Equal(expected, v);
        }
    }

    [Fact]
    public void WriteAndRead_Tombstone_RoundTrips()
    {
        var path = Path.Combine(_dir, "t.sst");
        SSTableWriter.Write(path, [new KeyValuePair<byte[], byte[]?>(Bytes("dead"), null)]);

        using var reader = new SSTableReader(path);
        // SSTableReader.TryGet returns true for tombstones (found, but value is null).
        // PithosDb interprets null value as deleted; the raw reader just signals presence.
        Assert.True(reader.TryGet(Bytes("dead"), out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ReadAllEntries_ReturnsAllEntries_WithChecksums()
    {
        var path = Path.Combine(_dir, "t.sst");
        var entries = Enumerable.Range(0, 50)
            .Select(i => new KeyValuePair<byte[], byte[]?>(
                BitConverter.GetBytes(i),
                BitConverter.GetBytes(i * 100)))
            .OrderBy(e => e.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ToList();

        SSTableWriter.Write(path, entries);

        using var reader = new SSTableReader(path);
        var read = reader.ReadAllEntries().ToList();
        Assert.Equal(entries.Count, read.Count);
    }

    // ── Corruption detection ───────────────────────────────────────────────

    [Fact]
    public void TryGet_CorruptBlockData_ThrowsInvalidDataException()
    {
        var path = Path.Combine(_dir, "t.sst");
        SSTableWriter.Write(path, [
            new KeyValuePair<byte[], byte[]?>(Bytes("hello"), Bytes("world")),
        ]);

        // Flip bits in the block data area (offset 5 is deep inside the entry, not the CRC).
        var bytes = File.ReadAllBytes(path);
        bytes[5] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var reader = new SSTableReader(path);
        Assert.Throws<InvalidDataException>(() => reader.TryGet(Bytes("hello"), out _));
    }

    [Fact]
    public void TryGet_CorruptBlockCrc_ThrowsInvalidDataException()
    {
        var path = Path.Combine(_dir, "t.sst");
        SSTableWriter.Write(path, [
            new KeyValuePair<byte[], byte[]?>(Bytes("hello"), Bytes("world")),
        ]);

        // Read to find where the bloom filter starts (= where the first block ends).
        long bloomOffset;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            fs.Seek(-16, SeekOrigin.End);
            using var br = new BinaryReader(fs);
            bloomOffset = br.ReadInt64();
        }

        // The last 4 bytes of the block (just before bloomOffset) are the CRC.
        var bytes = File.ReadAllBytes(path);
        bytes[bloomOffset - 1] ^= 0xFF; // corrupt the CRC
        File.WriteAllBytes(path, bytes);

        using var reader = new SSTableReader(path);
        Assert.Throws<InvalidDataException>(() => reader.TryGet(Bytes("hello"), out _));
    }
}
