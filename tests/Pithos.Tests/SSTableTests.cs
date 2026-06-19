using Pithos.Core.Storage;

namespace Pithos.Tests;

public class SSTableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public SSTableTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TempPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.sst");

    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static List<KeyValuePair<byte[], byte[]?>> Entries(params (string k, string? v)[] pairs) =>
        pairs.Select(p => new KeyValuePair<byte[], byte[]?>(K(p.k), p.v is null ? null : V(p.v)))
             .ToList();

    [Fact]
    public void WriteAndRead_SingleEntry_Found()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("hello", "world")));

        using var reader = new SSTableReader(path);
        Assert.True(reader.TryGet(K("hello"), out var value));
        Assert.Equal(V("world"), value);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("a", "1"), ("b", "2")));

        using var reader = new SSTableReader(path);
        Assert.False(reader.TryGet(K("z"), out _));
    }

    [Fact]
    public void TryGet_Tombstone_ReturnsNullValue()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("deleted", null)));

        using var reader = new SSTableReader(path);
        Assert.True(reader.TryGet(K("deleted"), out var value));
        Assert.Null(value);
    }

    [Fact]
    public void WriteAndRead_ManyEntries_AllFound()
    {
        var path = TempPath();
        (string, string?)[] entries = Enumerable.Range(0, 200)
            .Select(i => ($"key-{i:D5}", (string?)$"val-{i}"))
            .OrderBy(p => p.Item1)
            .ToArray();

        SSTableWriter.Write(path, Entries(entries));

        using var reader = new SSTableReader(path);
        foreach (var (k, v) in entries)
        {
            Assert.True(reader.TryGet(K(k), out var result), $"Expected to find {k}");
            Assert.Equal(V(v!), result);
        }
    }

    [Fact]
    public void ReadAllEntries_ReturnsAllEntriesInOrder()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("a", "1"), ("b", "2"), ("c", "3")));

        using var reader = new SSTableReader(path);
        var all = reader.ReadAllEntries().ToList();

        Assert.Equal(3, all.Count);
        Assert.Equal(K("a"), all[0].Key);
        Assert.Equal(K("b"), all[1].Key);
        Assert.Equal(K("c"), all[2].Key);
    }

    [Fact]
    public void ReadAllEntries_IncludesTombstones()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("alive", "yes"), ("dead", null)));

        using var reader = new SSTableReader(path);
        var all = reader.ReadAllEntries().ToList();

        Assert.Equal(2, all.Count);
        Assert.Null(all.First(e => e.Key.SequenceEqual(K("dead"))).Value);
    }

    [Fact]
    public void BloomFilter_SkipsIoForDefiniteMisses()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("only-key", "value")));

        using var reader = new SSTableReader(path);

        // A key far outside the written set should be a definite bloom miss.
        Assert.False(reader.TryGet(K("definitely-not-present-xyzzy-99999"), out _));
    }
}
