using PithosDB.Core;

namespace PithosDB.Tests;

public class ScanDescendingTests
{
    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ScanDescending_EmptyDb_ReturnsEmpty()
    {
        using var db = PithosDb.OpenInMemory();
        Assert.Empty(db.ScanDescending());
    }

    [Fact]
    public void ScanDescending_MultipleKeys_ReturnsDescendingOrder()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));

        var results = db.ScanDescending().ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal(K("c"), results[0].key);
        Assert.Equal(K("b"), results[1].key);
        Assert.Equal(K("a"), results[2].key);
    }

    [Fact]
    public void ScanDescending_WithFromBound_StartsAtUpperEnd()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));

        var results = db.ScanDescending(from: K("b")).ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(K("c"), results[0].key);
        Assert.Equal(K("b"), results[1].key);
    }

    [Fact]
    public void ScanDescending_WithToBound_StopsAtLowerEnd()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));

        var results = db.ScanDescending(to: K("b")).ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(K("b"), results[0].key);
        Assert.Equal(K("a"), results[1].key);
    }

    [Fact]
    public void ScanDescending_WithBothBounds_ReturnsRangeDescending()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));
        db.Put(K("d"), V("4"));

        var results = db.ScanDescending(from: K("b"), to: K("c")).ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(K("c"), results[0].key);
        Assert.Equal(K("b"), results[1].key);
    }

    [Fact]
    public void ScanDescending_SingleKey_ReturnsSingleEntry()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("only"), V("value"));

        var results = db.ScanDescending().ToList();
        Assert.Single(results);
        Assert.Equal(K("only"), results[0].key);
    }

    [Fact]
    public void ScanDescending_SkipsTombstones()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));
        db.Delete(K("b"));

        var results = db.ScanDescending().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(K("c"), results[0].key);
        Assert.Equal(K("a"), results[1].key);
    }

    [Fact]
    public void ScanDescending_ValuesMatch()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("alpha"));
        db.Put(K("b"), V("beta"));

        var results = db.ScanDescending().ToList();
        Assert.Equal(V("beta"), results[0].value);
        Assert.Equal(V("alpha"), results[1].value);
    }

    [Fact]
    public async Task ScanDescendingAsync_ReturnsDescendingOrder()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("x"), V("1"));
        db.Put(K("y"), V("2"));
        db.Put(K("z"), V("3"));

        var results = new List<(byte[] key, byte[] value)>();
        await foreach (var entry in db.ScanDescendingAsync())
            results.Add(entry);

        Assert.Equal(3, results.Count);
        Assert.Equal(K("z"), results[0].key);
        Assert.Equal(K("y"), results[1].key);
        Assert.Equal(K("x"), results[2].key);
    }

    [Fact]
    public async Task ScanDescendingAsync_WithBounds_ReturnsRangeDescending()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));

        var results = new List<(byte[] key, byte[] value)>();
        await foreach (var entry in db.ScanDescendingAsync(from: K("a"), to: K("b")))
            results.Add(entry);

        Assert.Equal(2, results.Count);
        Assert.Equal(K("b"), results[0].key);
        Assert.Equal(K("a"), results[1].key);
    }

    [Fact]
    public async Task ScanDescendingAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var db = PithosDb.OpenInMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in db.ScanDescendingAsync(cancellationToken: cts.Token)) { }
        });
    }
}
