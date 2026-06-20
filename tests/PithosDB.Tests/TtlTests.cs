using PithosDB.Core;

namespace PithosDB.Tests;

public class TtlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly PithosOptions _opts = new() { EnableTtl = true, MemTableSizeThreshold = 1024 };

    public TtlTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── Read-time expiry ───────────────────────────────────────────────────

    [Fact]
    public void Put_WithTtl_IsReadableBeforeExpiry()
    {
        using var db = new PithosDb(_dir, _opts);
        var key = "ttl-key"u8.ToArray();
        var val = "hello"u8.ToArray();

        db.Put(key, val, TimeSpan.FromSeconds(60));

        Assert.True(db.TryGet(key, out var result));
        Assert.Equal(val, result);
    }

    [Fact]
    public void Put_WithTtl_NotFoundAfterExpiry()
    {
        using var db = new PithosDb(_dir, _opts);
        var key = "ttl-key"u8.ToArray();

        db.Put(key, "hello"u8.ToArray(), TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);

        Assert.False(db.TryGet(key, out _));
    }

    [Fact]
    public void Put_WithoutTtl_IsAlwaysReadable()
    {
        using var db = new PithosDb(_dir, _opts);
        var key = "plain-key"u8.ToArray();
        var val = "world"u8.ToArray();

        db.Put(key, val); // no TTL

        Assert.True(db.TryGet(key, out var result));
        Assert.Equal(val, result);
    }

    [Fact]
    public void Put_WithTtl_ReturnsUserValue_NotEncodedBytes()
    {
        using var db = new PithosDb(_dir, _opts);
        var key = "k"u8.ToArray();
        var val = new byte[] { 0x01, 0x02, 0x03 }; // first byte would clash with TTL flag

        db.Put(key, val, TimeSpan.FromSeconds(60));

        Assert.True(db.TryGet(key, out var result));
        Assert.Equal(val, result);
    }

    [Fact]
    public void Put_WithTtl_ExcludedFromScan_AfterExpiry()
    {
        using var db = new PithosDb(_dir, _opts);
        db.Put("a"u8.ToArray(), "1"u8.ToArray(), TimeSpan.FromMilliseconds(1));
        db.Put("b"u8.ToArray(), "2"u8.ToArray(), TimeSpan.FromSeconds(60));

        Thread.Sleep(20);

        var results = db.Scan().ToList();
        Assert.Single(results);
        Assert.Equal("b"u8.ToArray(), results[0].key);
    }

    // ── TTL disabled guard ─────────────────────────────────────────────────

    [Fact]
    public void Put_WithTtl_Throws_WhenEnableTtlFalse()
    {
        var opts = new PithosOptions { EnableTtl = false };
        using var db = new PithosDb(_dir, opts);

        Assert.Throws<InvalidOperationException>(
            () => db.Put("k"u8.ToArray(), "v"u8.ToArray(), TimeSpan.FromSeconds(1)));
    }

    // ── WriteBatch TTL ─────────────────────────────────────────────────────

    [Fact]
    public void WriteBatch_WithTtl_ExpiresCorrectly()
    {
        using var db = new PithosDb(_dir, _opts);
        var batch = new WriteBatch()
            .Put("x"u8.ToArray(), "val-x"u8.ToArray(), TimeSpan.FromMilliseconds(1))
            .Put("y"u8.ToArray(), "val-y"u8.ToArray(), TimeSpan.FromSeconds(60));

        db.Write(batch);
        Thread.Sleep(20);

        Assert.False(db.TryGet("x"u8.ToArray(), out _));
        Assert.True(db.TryGet("y"u8.ToArray(), out _));
    }

    // ── WAL replay ────────────────────────────────────────────────────────

    [Fact]
    public void TtlEntry_SurvivesWalReplay_IfNotExpired()
    {
        var key = "k"u8.ToArray();
        var val = "v"u8.ToArray();

        using (var db = new PithosDb(_dir, _opts))
            db.Put(key, val, TimeSpan.FromSeconds(60));

        using (var db = new PithosDb(_dir, _opts))
        {
            Assert.True(db.TryGet(key, out var result));
            Assert.Equal(val, result);
        }
    }

    [Fact]
    public void TtlEntry_NotFound_OnReopen_AfterExpiry()
    {
        var key = "k"u8.ToArray();

        using (var db = new PithosDb(_dir, _opts))
            db.Put(key, "v"u8.ToArray(), TimeSpan.FromMilliseconds(1));

        Thread.Sleep(20);

        using (var db = new PithosDb(_dir, _opts))
            Assert.False(db.TryGet(key, out _));
    }

    // ── Compaction physical removal ────────────────────────────────────────

    [Fact]
    public void TtlEntry_DroppedDuringCompaction()
    {
        // Low flush threshold so we get SSTables quickly.
        var opts = new PithosOptions
        {
            EnableTtl = true,
            MemTableSizeThreshold = 512,
            LevelZeroFileCountLimit = 2,
        };

        using var db = new PithosDb(_dir, opts);

        // Write enough entries to trigger a flush + compaction.
        // Include one that will expire before compaction runs.
        db.Put("expire-me"u8.ToArray(), new byte[256], TimeSpan.FromMilliseconds(1));
        for (int i = 0; i < 20; i++)
            db.Put(BitConverter.GetBytes(i), new byte[64]);

        Thread.Sleep(50); // ensure the TTL entry has expired

        // More writes to trigger another flush and compaction.
        for (int i = 20; i < 40; i++)
            db.Put(BitConverter.GetBytes(i), new byte[64]);

        Assert.False(db.TryGet("expire-me"u8.ToArray(), out _));
    }
}
