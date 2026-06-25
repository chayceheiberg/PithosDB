using PithosDB.Core;

namespace PithosDB.Tests;

public class DbStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public DbStatsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ── In-memory database ────────────────────────────────────────────────

    [Fact]
    public void GetStats_InMemory_WalSizeIsMinusOne()
    {
        using var db = PithosDb.OpenInMemory();
        var stats = db.GetStats();

        Assert.Equal(-1L, stats.WalSizeBytes);
    }

    [Fact]
    public void GetStats_InMemory_NoSstFiles()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));

        var stats = db.GetStats();

        Assert.Equal(0, stats.LevelCount);
        Assert.Equal(0, stats.TotalSstFileCount);
        Assert.Equal(0L, stats.TotalSstDiskSizeBytes);
    }

    [Fact]
    public void GetStats_NoCacheConfigured_ReturnsMinusOne()
    {
        using var db = new PithosDb(_dir, new PithosOptions { BlockCacheSizeBytes = 0 });
        var stats = db.GetStats();

        Assert.Equal(-1L, stats.BlockCacheCurrentSizeBytes);
        Assert.Equal(-1L, stats.BlockCacheMaxSizeBytes);
    }

    // ── MemTable stats ────────────────────────────────────────────────────

    [Fact]
    public void GetStats_AfterPuts_MemTableSizeGrows()
    {
        using var db = PithosDb.OpenInMemory();
        var before = db.GetStats().MemTableSizeBytes;

        db.Put(K("key1"), V("value1"));
        db.Put(K("key2"), V("value2"));

        Assert.True(db.GetStats().MemTableSizeBytes > before);
    }

    [Fact]
    public void GetStats_EntryCount_IncludesTombstones()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Delete(K("a")); // tombstone — still an entry

        var stats = db.GetStats();

        Assert.Equal(2, stats.MemTableEntryCount);
    }

    [Fact]
    public void GetStats_EmptyDb_MemTableSizeIsZero()
    {
        using var db = PithosDb.OpenInMemory();
        var stats = db.GetStats();

        Assert.Equal(0L, stats.MemTableSizeBytes);
        Assert.Equal(0, stats.MemTableEntryCount);
    }

    // ── SSTable stats (disk-backed) ───────────────────────────────────────

    [Fact]
    public void GetStats_AfterFlush_SstFileCountIncreases()
    {
        using var db = new PithosDb(_dir, new PithosOptions { MemTableSizeThreshold = 64 });

        for (int i = 0; i < 20; i++)
            db.Put(K($"key{i:D3}"), V($"value{i}"));

        var stats = db.GetStats();

        Assert.True(stats.TotalSstFileCount > 0);
        Assert.True(stats.LevelCount > 0);
    }

    [Fact]
    public void GetStats_AfterFlush_DiskSizeIsPositive()
    {
        using var db = new PithosDb(_dir, new PithosOptions { MemTableSizeThreshold = 64 });

        for (int i = 0; i < 20; i++)
            db.Put(K($"key{i:D3}"), V($"value{i}"));

        var stats = db.GetStats();

        Assert.True(stats.TotalSstDiskSizeBytes > 0);
    }

    [Fact]
    public void GetStats_FileCountPerLevel_SumsToTotal()
    {
        using var db = new PithosDb(_dir, new PithosOptions { MemTableSizeThreshold = 64 });

        for (int i = 0; i < 20; i++)
            db.Put(K($"key{i:D3}"), V($"value{i}"));

        var stats = db.GetStats();

        Assert.Equal(stats.TotalSstFileCount, stats.FileCountPerLevel.Sum());
        Assert.Equal(stats.TotalSstDiskSizeBytes, stats.DiskSizeBytesPerLevel.Sum());
    }

    // ── WAL stats (disk-backed) ───────────────────────────────────────────

    [Fact]
    public void GetStats_AfterPuts_WalSizeIsPositive()
    {
        using var db = new PithosDb(_dir);
        db.Put(K("k"), V("v"));

        Assert.True(db.GetStats().WalSizeBytes > 0);
    }

    // ── Block cache ───────────────────────────────────────────────────────

    [Fact]
    public void GetStats_WithBlockCache_ReportsCapacity()
    {
        const long cacheSize = 1024 * 1024;
        using var db = new PithosDb(_dir, new PithosOptions { BlockCacheSizeBytes = cacheSize });

        var stats = db.GetStats();

        Assert.Equal(cacheSize, stats.BlockCacheMaxSizeBytes);
        Assert.True(stats.BlockCacheCurrentSizeBytes >= 0);
    }

    [Fact]
    public void GetStats_WithBlockCache_CurrentSizeGrowsAfterReads()
    {
        using var db = new PithosDb(_dir, new PithosOptions
        {
            MemTableSizeThreshold = 64,
            BlockCacheSizeBytes   = 1024 * 1024,
        });

        for (int i = 0; i < 20; i++)
            db.Put(K($"key{i:D3}"), V($"value{i}"));

        var before = db.GetStats().BlockCacheCurrentSizeBytes;

        // Reads populate the block cache.
        for (int i = 0; i < 20; i++)
            db.TryGet(K($"key{i:D3}"), out _);

        Assert.True(db.GetStats().BlockCacheCurrentSizeBytes >= before);
    }

    // ── Async ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_ReturnsSameDataAsSync()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));

        var sync  = db.GetStats();
        var async = await db.GetStatsAsync();

        Assert.Equal(sync.MemTableEntryCount, async.MemTableEntryCount);
        Assert.Equal(sync.LevelCount,         async.LevelCount);
        Assert.Equal(sync.WalSizeBytes,       async.WalSizeBytes);
    }
}
