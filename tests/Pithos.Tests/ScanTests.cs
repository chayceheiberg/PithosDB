using System.Text;
using Pithos.Core;

namespace Pithos.Tests;

public class ScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly PithosDb _db;

    public ScanTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new PithosDb(_dir);

        // Seed: keys "a" through "e"
        foreach (var ch in "abcde")
            _db.Put(K(ch.ToString()), V(ch.ToString()));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void Scan_NoBounds_ReturnsAllLiveEntries()
    {
        var results = _db.Scan().ToList();

        Assert.Equal(5, results.Count);
        Assert.Equal(["a", "b", "c", "d", "e"], results.Select(r => Str(r.Key)));
    }

    [Fact]
    public void Scan_WithFromAndTo_ReturnsRange()
    {
        var results = _db.Scan(K("b"), K("d")).ToList();

        Assert.Equal(["b", "c", "d"], results.Select(r => Str(r.Key)));
    }

    [Fact]
    public void Scan_FromOnly_ReturnsFromBoundToEnd()
    {
        var results = _db.Scan(from: K("c")).ToList();

        Assert.Equal(["c", "d", "e"], results.Select(r => Str(r.Key)));
    }

    [Fact]
    public void Scan_ToOnly_ReturnsStartToBound()
    {
        var results = _db.Scan(to: K("c")).ToList();

        Assert.Equal(["a", "b", "c"], results.Select(r => Str(r.Key)));
    }

    [Fact]
    public void Scan_ExactKey_ReturnsSingleEntry()
    {
        var results = _db.Scan(K("c"), K("c")).ToList();

        Assert.Single(results);
        Assert.Equal("c", Str(results[0].Key));
    }

    [Fact]
    public void Scan_BeyondAllKeys_ReturnsEmpty()
    {
        var results = _db.Scan(K("z"), K("z")).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_ExcludesDeletedKeys()
    {
        _db.Delete(K("c"));

        var results = _db.Scan(K("b"), K("d")).ToList();

        Assert.Equal(["b", "d"], results.Select(r => Str(r.Key)));
    }

    [Fact]
    public void Scan_ReturnsCorrectValues()
    {
        var results = _db.Scan(K("a"), K("b")).ToList();

        Assert.Equal(V("a"), results[0].Value);
        Assert.Equal(V("b"), results[1].Value);
    }

    [Fact]
    public void Scan_ReturnsEntriesInSortedOrder()
    {
        var results = _db.Scan().ToList();
        var keys = results.Select(r => r.Key).ToList();

        for (int i = 1; i < keys.Count; i++)
            Assert.True(keys[i - 1].AsSpan().SequenceCompareTo(keys[i].AsSpan()) < 0);
    }

    [Fact]
    public void Scan_OverwrittenKey_ReturnsLatestValue()
    {
        _db.Put(K("c"), V("updated"));

        var results = _db.Scan(K("c"), K("c")).ToList();

        Assert.Single(results);
        Assert.Equal(V("updated"), results[0].Value);
    }

    [Fact]
    public void Scan_AcrossFlush_ReturnsAllEntries()
    {
        // Force a flush by writing enough data to exceed the default 4 MB threshold.
        // Use "z-" prefix so these keys sort after "e" and don't pollute the scan range.
        var bigValue = new byte[1024];
        for (int i = 0; i < 5000; i++)
            _db.Put(Encoding.UTF8.GetBytes($"z-{i:D5}"), bigValue);

        // The earlier "a"–"e" entries may be in an SSTable; the big-* entries span MemTable + SSTables.
        var results = _db.Scan(K("a"), K("e")).ToList();

        Assert.Equal(["a", "b", "c", "d", "e"], results.Select(r => Str(r.Key)));
    }
}
