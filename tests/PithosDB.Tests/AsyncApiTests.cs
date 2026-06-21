using PithosDB.Core;

namespace PithosDB.Tests;

public class AsyncApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public AsyncApiTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ── PutAsync / GetAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_ThenGetAsync_ReturnsValue()
    {
        using var db = new PithosDb(_dir);
        await db.PutAsync(K("hello"), V("world"));

        var result = await db.GetAsync(K("hello"));
        Assert.Equal(V("world"), result);
    }

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsNull()
    {
        using var db = new PithosDb(_dir);
        Assert.Null(await db.GetAsync(K("missing")));
    }

    [Fact]
    public async Task PutAsync_OverwritesExistingKey()
    {
        using var db = new PithosDb(_dir);
        await db.PutAsync(K("key"), V("first"));
        await db.PutAsync(K("key"), V("second"));

        Assert.Equal(V("second"), await db.GetAsync(K("key")));
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_MakesKeyUnreadable()
    {
        using var db = new PithosDb(_dir);
        await db.PutAsync(K("key"), V("value"));
        await db.DeleteAsync(K("key"));

        Assert.Null(await db.GetAsync(K("key")));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_DoesNotThrow()
    {
        using var db = new PithosDb(_dir);
        await db.DeleteAsync(K("ghost"));
        Assert.Null(await db.GetAsync(K("ghost")));
    }

    // ── WriteAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_AppliesBatchAtomically()
    {
        using var db = new PithosDb(_dir);
        var batch = new WriteBatch()
            .Put(K("a"), V("1"))
            .Put(K("b"), V("2"))
            .Delete(K("c"));

        await db.PutAsync(K("c"), V("to-delete"));
        await db.WriteAsync(batch);

        Assert.Equal(V("1"), await db.GetAsync(K("a")));
        Assert.Equal(V("2"), await db.GetAsync(K("b")));
        Assert.Null(await db.GetAsync(K("c")));
    }

    [Fact]
    public async Task WriteAsync_EmptyBatch_DoesNotThrow()
    {
        using var db = new PithosDb(_dir);
        await db.WriteAsync(new WriteBatch());
    }

    // ── PutAsync with TTL ─────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_WithTtl_ExpiresCorrectly()
    {
        var opts = new PithosOptions { EnableTtl = true };
        using var db = new PithosDb(_dir, opts);

        await db.PutAsync(K("expiring"), V("value"), TimeSpan.FromMilliseconds(1));
        await db.PutAsync(K("permanent"), V("value"), TimeSpan.FromHours(1));

        await Task.Delay(20);

        Assert.Null(await db.GetAsync(K("expiring")));
        Assert.NotNull(await db.GetAsync(K("permanent")));
    }

    // ── ScanAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_ReturnsAllEntries()
    {
        using var db = new PithosDb(_dir);
        await db.PutAsync(K("a"), V("1"));
        await db.PutAsync(K("b"), V("2"));
        await db.PutAsync(K("c"), V("3"));

        var results = new List<(byte[] key, byte[] value)>();
        await foreach (var entry in db.ScanAsync())
            results.Add(entry);

        Assert.Equal(3, results.Count);
        Assert.Equal(V("1"), results.Single(e => e.key.SequenceEqual(K("a"))).value);
        Assert.Equal(V("2"), results.Single(e => e.key.SequenceEqual(K("b"))).value);
        Assert.Equal(V("3"), results.Single(e => e.key.SequenceEqual(K("c"))).value);
    }

    [Fact]
    public async Task ScanAsync_WithBounds_ReturnsCorrectRange()
    {
        using var db = new PithosDb(_dir);
        await db.PutAsync(K("a"), V("1"));
        await db.PutAsync(K("b"), V("2"));
        await db.PutAsync(K("c"), V("3"));
        await db.PutAsync(K("d"), V("4"));

        var results = new List<(byte[] key, byte[] value)>();
        await foreach (var entry in db.ScanAsync(from: K("b"), to: K("c")))
            results.Add(entry);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, e => e.key.SequenceEqual(K("b")));
        Assert.Contains(results, e => e.key.SequenceEqual(K("c")));
    }

    [Fact]
    public async Task ScanAsync_ExcludesDeletedKeys()
    {
        using var db = new PithosDb(_dir);
        await db.PutAsync(K("keep"), V("yes"));
        await db.PutAsync(K("drop"), V("no"));
        await db.DeleteAsync(K("drop"));

        var keys = new List<byte[]>();
        await foreach (var (key, _) in db.ScanAsync())
            keys.Add(key);

        Assert.Single(keys);
        Assert.True(keys[0].SequenceEqual(K("keep")));
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_CancelledToken_ThrowsOperationCancelled()
    {
        using var db = new PithosDb(_dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => db.GetAsync(K("key"), cts.Token));
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentPutAsync_DoesNotCorruptData()
    {
        using var db = new PithosDb(_dir);
        const int tasks = 8;
        const int writesPerTask = 50;

        await Task.WhenAll(Enumerable.Range(0, tasks).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < writesPerTask; i++)
                await db.PutAsync(K($"t{t}-k{i}"), V($"v{t}-{i}"));
        })));

        // Spot-check a few entries from each task.
        for (int t = 0; t < tasks; t++)
        {
            var result = await db.GetAsync(K($"t{t}-k0"));
            Assert.Equal(V($"v{t}-0"), result);
        }
    }
}
