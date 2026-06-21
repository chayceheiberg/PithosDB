using PithosDB.Core;

namespace PithosDB.Tests;

public class ApproximateCountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ApproximateCountTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(int i) => BitConverter.GetBytes(i);
    private static byte[] V(int i) => BitConverter.GetBytes(i * 10);

    // Force a flush by writing enough data to exceed the default 4 MB threshold.
    // Using a tiny threshold makes tests deterministic without large data sets.
    private static PithosOptions TinyFlush => new() { MemTableSizeThreshold = 64 };

    // ── Empty database ───────────────────────────────────────────────────

    [Fact]
    public void ApproximateCount_EmptyDb_ReturnsZero()
    {
        using var db = new PithosDb(_dir);
        Assert.Equal(0, db.ApproximateCount());
    }

    [Fact]
    public void ApproximateCount_EmptyDb_WithBounds_ReturnsZero()
    {
        using var db = new PithosDb(_dir);
        Assert.Equal(0, db.ApproximateCount(K(0), K(100)));
    }

    // ── MemTable only (no flush) ──────────────────────────────────────────

    [Fact]
    public void ApproximateCount_MemTableOnly_ExactCount()
    {
        using var db = new PithosDb(_dir);
        for (int i = 0; i < 10; i++) db.Put(K(i), V(i));

        Assert.Equal(10, db.ApproximateCount());
    }

    [Fact]
    public void ApproximateCount_MemTableOnly_WithBounds()
    {
        using var db = new PithosDb(_dir);
        for (int i = 0; i < 10; i++) db.Put(K(i), V(i));

        // Keys 3, 4, 5, 6 → 4 keys
        long count = db.ApproximateCount(K(3), K(6));
        Assert.Equal(4, count);
    }

    [Fact]
    public void ApproximateCount_MemTableOnly_TombstonesNotCounted()
    {
        using var db = new PithosDb(_dir);
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));
        db.Delete(K(2));

        Assert.Equal(4, db.ApproximateCount());
    }

    [Fact]
    public void ApproximateCount_MemTableOnly_OpenFromBound()
    {
        using var db = new PithosDb(_dir);
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        // from = null → all keys ≤ K(2)
        Assert.Equal(3, db.ApproximateCount(to: K(2)));
    }

    [Fact]
    public void ApproximateCount_MemTableOnly_OpenToBound()
    {
        using var db = new PithosDb(_dir);
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        // to = null → all keys ≥ K(3)
        Assert.Equal(2, db.ApproximateCount(from: K(3)));
    }

    // ── After SSTable flush ───────────────────────────────────────────────

    [Fact]
    public void ApproximateCount_AfterFlush_PositiveCount()
    {
        using var db = new PithosDb(_dir, TinyFlush);

        // Write enough to force a flush (TinyFlush threshold = 64 bytes).
        for (int i = 0; i < 20; i++) db.Put(K(i), V(i));

        long count = db.ApproximateCount();
        Assert.True(count > 0, $"expected positive count, got {count}");
    }

    [Fact]
    public void ApproximateCount_AfterFlush_WithBounds_PositiveCount()
    {
        using var db = new PithosDb(_dir, TinyFlush);
        for (int i = 0; i < 20; i++) db.Put(K(i), V(i));

        long count = db.ApproximateCount(K(5), K(15));
        Assert.True(count > 0, $"expected positive count in range, got {count}");
    }

    [Fact]
    public void ApproximateCount_BoundsOutsideData_LowCount()
    {
        using var db = new PithosDb(_dir, TinyFlush);
        for (int i = 0; i < 10; i++) db.Put(K(i), V(i));

        // Keys 1000-2000 don't exist. ApproximateCount may return a small non-zero
        // value (at most one per SSTable) because the upper bound of the final block
        // is unknown without reading its last entry — the last block always matches
        // any query whose upper bound exceeds the block's first key.
        long count = db.ApproximateCount(K(1000), K(2000));
        Assert.True(count <= 5, $"expected low count for out-of-range query, got {count}");
    }

    // ── In-memory mode ────────────────────────────────────────────────────

    [Fact]
    public void ApproximateCount_InMemory_ExactCount()
    {
        using var db = PithosDb.OpenInMemory();
        for (int i = 0; i < 7; i++) db.Put(K(i), V(i));

        Assert.Equal(7, db.ApproximateCount());
    }

    [Fact]
    public void ApproximateCount_InMemory_WithBounds()
    {
        using var db = PithosDb.OpenInMemory();
        for (int i = 0; i < 10; i++) db.Put(K(i), V(i));

        Assert.Equal(3, db.ApproximateCount(K(0), K(2)));
    }
}
