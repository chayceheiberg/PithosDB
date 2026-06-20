using Pithos.Core;
using Pithos.Core.Storage;

namespace Pithos.Tests;

public class CompressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public CompressionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static List<KeyValuePair<byte[], byte[]?>> MakeEntries(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new KeyValuePair<byte[], byte[]?>(
                System.Text.Encoding.UTF8.GetBytes($"key-{i:D6}"),
                System.Text.Encoding.UTF8.GetBytes($"value-{i}")))
            .OrderBy(e => e.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ToList();

    // ── SSTableWriter / SSTableReader round-trips ──────────────────────────

    [Fact]
    public void Lz4_WriteAndRead_AllValuesCorrect()
    {
        var path = Path.Combine(_dir, "lz4.sst");
        var entries = MakeEntries(200);

        SSTableWriter.Write(path, entries, compression: CompressionKind.Lz4);

        using var reader = new SSTableReader(path);
        foreach (var (key, expected) in entries)
        {
            Assert.True(reader.TryGet(key, out var value));
            Assert.Equal(expected, value);
        }
    }

    [Fact]
    public void Lz4_MissingKey_ReturnsFalse()
    {
        var path = Path.Combine(_dir, "lz4_miss.sst");
        SSTableWriter.Write(path, MakeEntries(50), compression: CompressionKind.Lz4);

        using var reader = new SSTableReader(path);
        Assert.False(reader.TryGet("zzz-missing"u8.ToArray(), out _));
    }

    [Fact]
    public void Lz4_Tombstone_RoundTrips()
    {
        var path = Path.Combine(_dir, "lz4_tomb.sst");
        var entries = new List<KeyValuePair<byte[], byte[]?>>
        {
            new("alive"u8.ToArray(), "value"u8.ToArray()),
            new("dead"u8.ToArray(),  null),
        };
        SSTableWriter.Write(path, entries, compression: CompressionKind.Lz4);

        using var reader = new SSTableReader(path);
        Assert.True(reader.TryGet("alive"u8.ToArray(), out var v));
        Assert.Equal("value"u8.ToArray(), v);

        Assert.True(reader.TryGet("dead"u8.ToArray(), out var tomb));
        Assert.Null(tomb);
    }

    [Fact]
    public void Lz4_ReadAllEntries_ReturnsAllInOrder()
    {
        var path = Path.Combine(_dir, "lz4_all.sst");
        var entries = MakeEntries(100);
        SSTableWriter.Write(path, entries, compression: CompressionKind.Lz4);

        using var reader = new SSTableReader(path);
        var result = reader.ReadAllEntries().ToList();

        Assert.Equal(entries.Count, result.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].Key, result[i].Key);
            Assert.Equal(entries[i].Value, result[i].Value);
        }
    }

    [Fact]
    public void Lz4_FileIsSmallerThanUncompressed()
    {
        var pathNone = Path.Combine(_dir, "none.sst");
        var pathLz4  = Path.Combine(_dir, "lz4.sst");
        var entries  = MakeEntries(500);

        SSTableWriter.Write(pathNone, entries, compression: CompressionKind.None);
        SSTableWriter.Write(pathLz4,  entries, compression: CompressionKind.Lz4);

        long sizeNone = new FileInfo(pathNone).Length;
        long sizeLz4  = new FileInfo(pathLz4).Length;

        Assert.True(sizeLz4 < sizeNone,
            $"Expected LZ4 file ({sizeLz4} B) to be smaller than uncompressed ({sizeNone} B).");
    }

    // ── End-to-end via PithosDb ────────────────────────────────────────────

    [Fact]
    public void PithosDb_Lz4_PutAndGet_RoundTrips()
    {
        var opts = new PithosOptions
        {
            Compression           = CompressionKind.Lz4,
            MemTableSizeThreshold = 4 * 1024,
        };
        var dbDir = Path.Combine(_dir, "db_lz4");

        using (var db = new PithosDb(dbDir, opts))
        {
            for (int i = 0; i < 200; i++)
                db.Put(System.Text.Encoding.UTF8.GetBytes($"k{i:D4}"), System.Text.Encoding.UTF8.GetBytes($"v{i}"));
        }

        using (var db = new PithosDb(dbDir, opts))
        {
            for (int i = 0; i < 200; i++)
            {
                Assert.True(db.TryGet(System.Text.Encoding.UTF8.GetBytes($"k{i:D4}"), out var val));
                Assert.Equal(System.Text.Encoding.UTF8.GetBytes($"v{i}"), val);
            }
        }
    }

    [Fact]
    public void PithosDb_Lz4_DeleteAndReopen_KeyGone()
    {
        var opts = new PithosOptions
        {
            Compression           = CompressionKind.Lz4,
            MemTableSizeThreshold = 1024,
        };
        var dbDir = Path.Combine(_dir, "db_lz4_del");

        using (var db = new PithosDb(dbDir, opts))
        {
            db.Put("hello"u8.ToArray(), "world"u8.ToArray());
            db.Delete("hello"u8.ToArray());
        }

        using (var db = new PithosDb(dbDir, opts))
        {
            Assert.False(db.TryGet("hello"u8.ToArray(), out _));
        }
    }
}
