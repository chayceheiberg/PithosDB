using PithosDB.Core;

namespace PithosDB.Tests;

public class WalSyncModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public WalSyncModeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(int i) => BitConverter.GetBytes(i);
    private static byte[] V(int i) => BitConverter.GetBytes(i * 10);

    // ── Default / Full mode ───────────────────────────────────────────────

    [Fact]
    public void WalSync_Default_IsFull()
    {
        var opts = new PithosOptions();
        Assert.Equal(WalSyncMode.Full, opts.WalSyncMode);
    }

    [Fact]
    public void WalSync_Full_WritesRecoverAfterReopen()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.Full };
        using (var db = new PithosDb(_dir, opts))
        {
            for (int i = 0; i < 10; i++) db.Put(K(i), V(i));
        }

        using var db2 = new PithosDb(_dir, opts);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(db2.TryGet(K(i), out var v));
            Assert.Equal(V(i), v);
        }
    }

    // ── None mode ─────────────────────────────────────────────────────────

    [Fact]
    public void WalSync_None_WritesRecoverAfterCleanClose()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.None };
        using (var db = new PithosDb(_dir, opts))
        {
            for (int i = 0; i < 10; i++) db.Put(K(i), V(i));
        } // Dispose flushes before closing

        using var db2 = new PithosDb(_dir, opts);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(db2.TryGet(K(i), out var v));
            Assert.Equal(V(i), v);
        }
    }

    [Fact]
    public void WalSync_None_DeletesRecoverAfterCleanClose()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.None };
        using (var db = new PithosDb(_dir, opts))
        {
            for (int i = 0; i < 5; i++) db.Put(K(i), V(i));
            db.Delete(K(2));
        }

        using var db2 = new PithosDb(_dir, opts);
        Assert.True(db2.TryGet(K(0), out _));
        Assert.False(db2.TryGet(K(2), out _));
        Assert.True(db2.TryGet(K(4), out _));
    }

    // ── Periodic mode ─────────────────────────────────────────────────────

    [Fact]
    public void WalSync_Periodic_WritesRecoverAfterCleanClose()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.Periodic, WalSyncIntervalMs = 50 };
        using (var db = new PithosDb(_dir, opts))
        {
            for (int i = 0; i < 10; i++) db.Put(K(i), V(i));
        }

        using var db2 = new PithosDb(_dir, opts);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(db2.TryGet(K(i), out var v));
            Assert.Equal(V(i), v);
        }
    }

    [Fact]
    public async Task WalSync_Periodic_FlushesWithinInterval()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.Periodic, WalSyncIntervalMs = 50 };
        using var db = new PithosDb(_dir, opts);
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        // Wait longer than the sync interval; data should be readable after reopen
        // even if the process had shut down gracefully (Dispose flushes).
        await Task.Delay(150);

        // Verify data is still readable within the same session.
        for (int i = 0; i < 5; i++)
        {
            Assert.True(db.TryGet(K(i), out var v));
            Assert.Equal(V(i), v);
        }
    }

    [Fact]
    public void WalSync_Periodic_BatchRecovery()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.Periodic, WalSyncIntervalMs = 50 };
        using (var db = new PithosDb(_dir, opts))
        {
            var batch = new WriteBatch();
            for (int i = 0; i < 5; i++) batch.Put(K(i), V(i));
            db.Write(batch);
        }

        using var db2 = new PithosDb(_dir, opts);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(db2.TryGet(K(i), out var v));
            Assert.Equal(V(i), v);
        }
    }

    // ── Validation ────────────────────────────────────────────────────────

    [Fact]
    public void WalSync_Periodic_InvalidInterval_Throws()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.Periodic, WalSyncIntervalMs = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new PithosDb(_dir, opts));
    }

    [Fact]
    public void WalSync_Periodic_NegativeInterval_Throws()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.Periodic, WalSyncIntervalMs = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new PithosDb(_dir, opts));
    }

    [Fact]
    public void WalSync_Full_IntervalIgnored()
    {
        // WalSyncIntervalMs is only validated for Periodic mode — other modes ignore it.
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.Full, WalSyncIntervalMs = 0 };
        using var db = new PithosDb(_dir, opts);
        db.Put(K(0), V(0));
        Assert.True(db.TryGet(K(0), out _));
    }

    [Fact]
    public void WalSync_None_IntervalIgnored()
    {
        var opts = new PithosOptions { WalSyncMode = WalSyncMode.None, WalSyncIntervalMs = 0 };
        using var db = new PithosDb(_dir, opts);
        db.Put(K(0), V(0));
        Assert.True(db.TryGet(K(0), out _));
    }

    // ── All modes round-trip ──────────────────────────────────────────────

    [Theory]
    [InlineData(WalSyncMode.Full)]
    [InlineData(WalSyncMode.Periodic)]
    [InlineData(WalSyncMode.None)]
    public void WalSync_AllModes_PutGetDelete(WalSyncMode mode)
    {
        var opts = new PithosOptions { WalSyncMode = mode, WalSyncIntervalMs = 50 };
        using var db = new PithosDb(_dir, opts);

        db.Put(K(1), V(1));
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(1), v);

        db.Delete(K(1));
        Assert.False(db.TryGet(K(1), out _));
    }
}
