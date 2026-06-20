using Pithos.Core.Core;
using Pithos.Core.Storage;

namespace Pithos.Tests;

public class BlockCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public BlockCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Block(int size, byte fill = 0)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    // ── Unit tests for LruBlockCache ──────────────────────────────────────────

    [Fact]
    public void TryGet_ReturnsFalse_WhenNotCached()
    {
        var cache = new LruBlockCache(1024);
        Assert.False(cache.TryGet("file.sst", 0, out _));
    }

    [Fact]
    public void TryGet_ReturnsBlock_AfterPut()
    {
        var cache = new LruBlockCache(1024);
        var data = Block(64, fill: 7);
        cache.Put("file.sst", 0, data);

        Assert.True(cache.TryGet("file.sst", 0, out var result));
        Assert.Equal(data, result);
    }

    [Fact]
    public void Put_SecondCallForSameKey_IsNoOp()
    {
        var cache = new LruBlockCache(1024);
        var first  = Block(64, fill: 1);
        var second = Block(64, fill: 2);

        cache.Put("file.sst", 0, first);
        cache.Put("file.sst", 0, second);

        cache.TryGet("file.sst", 0, out var result);
        Assert.Equal(first, result);
    }

    [Fact]
    public void LRU_EvictsOldestBlock_WhenCapacityExceeded()
    {
        // Capacity for exactly two 64-byte blocks.
        var cache = new LruBlockCache(128);
        cache.Put("f", 0,   Block(64, fill: 1)); // oldest
        cache.Put("f", 64,  Block(64, fill: 2)); // capacity full

        // Adding a third block must evict the oldest (offset 0).
        cache.Put("f", 128, Block(64, fill: 3));

        Assert.False(cache.TryGet("f", 0,   out _), "oldest should be evicted");
        Assert.True (cache.TryGet("f", 64,  out _), "second should remain");
        Assert.True (cache.TryGet("f", 128, out _), "newest should be present");
    }

    [Fact]
    public void LRU_AccessRefreshesOrder_PreventingEviction()
    {
        // Capacity for two blocks.
        var cache = new LruBlockCache(128);
        cache.Put("f", 0,  Block(64, fill: 1)); // A — will be refreshed
        cache.Put("f", 64, Block(64, fill: 2)); // B

        // Access A — it becomes most-recently-used; B becomes the eviction candidate.
        cache.TryGet("f", 0, out _);

        // Adding C should evict B, not A.
        cache.Put("f", 128, Block(64, fill: 3));

        Assert.True (cache.TryGet("f", 0,   out _), "A was recently accessed, should remain");
        Assert.False(cache.TryGet("f", 64,  out _), "B was least-recently-used, should be evicted");
        Assert.True (cache.TryGet("f", 128, out _), "C should be present");
    }

    [Fact]
    public void EvictFile_RemovesAllBlocksForThatPath()
    {
        var cache = new LruBlockCache(1024);
        cache.Put("a.sst", 0,   Block(64));
        cache.Put("a.sst", 64,  Block(64));
        cache.Put("b.sst", 0,   Block(64));

        cache.EvictFile("a.sst");

        Assert.False(cache.TryGet("a.sst", 0,  out _));
        Assert.False(cache.TryGet("a.sst", 64, out _));
        Assert.True (cache.TryGet("b.sst", 0,  out _), "other file unaffected");
    }

    [Fact]
    public void EvictFile_NoOp_WhenFileNotCached()
    {
        var cache = new LruBlockCache(1024);
        cache.Put("a.sst", 0, Block(64));
        cache.EvictFile("b.sst"); // should not throw or disturb a.sst

        Assert.True(cache.TryGet("a.sst", 0, out _));
    }

    [Fact]
    public async Task ConcurrentPutsAndGets_DoNotCorruptState()
    {
        var cache = new LruBlockCache(64 * 200);
        const int threads = 8;
        const int opsPerThread = 500;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                long offset = (t * opsPerThread + i) * 64L;
                cache.Put("f", offset, Block(64, fill: (byte)(offset % 256)));
                cache.TryGet("f", offset, out _);
            }
        })));
    }

    // ── Integration: SSTableReader with BlockCache ─────────────────────────

    [Fact]
    public void SSTableReader_WithCache_ReturnsCorrectValues()
    {
        var path = Path.Combine(_dir, "test.sst");
        var entries = Enumerable.Range(0, 50)
            .Select(i => new KeyValuePair<byte[], byte[]?>(
                System.Text.Encoding.UTF8.GetBytes($"key-{i:D4}"),
                System.Text.Encoding.UTF8.GetBytes($"val-{i}")))
            .OrderBy(e => e.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ToList();

        SSTableWriter.Write(path, entries);

        var cache = new LruBlockCache(1024 * 1024);
        using var reader = new SSTableReader(path, cache);

        // First pass — cold cache, blocks read from disk and populated.
        foreach (var (key, expected) in entries)
        {
            Assert.True(reader.TryGet(key, out var value));
            Assert.Equal(expected, value);
        }

        // Second pass — blocks served from cache, results must be identical.
        foreach (var (key, expected) in entries)
        {
            Assert.True(reader.TryGet(key, out var value));
            Assert.Equal(expected, value);
        }
    }

    [Fact]
    public void SSTableReader_WithCache_MissingKeyReturnsFalse()
    {
        var path = Path.Combine(_dir, "test.sst");
        SSTableWriter.Write(path, [
            new KeyValuePair<byte[], byte[]?>(
                System.Text.Encoding.UTF8.GetBytes("hello"),
                System.Text.Encoding.UTF8.GetBytes("world"))
        ]);

        var cache = new LruBlockCache(1024 * 1024);
        using var reader = new SSTableReader(path, cache);

        // Cold miss, then warm miss (block cached but key not in it).
        Assert.False(reader.TryGet(System.Text.Encoding.UTF8.GetBytes("zzz"), out _));
        Assert.False(reader.TryGet(System.Text.Encoding.UTF8.GetBytes("zzz"), out _));
    }
}
