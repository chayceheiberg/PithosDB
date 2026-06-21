using PithosDB.Core;

namespace PithosDB.Tests;

public class CompactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public CompactionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Flush every ~32 entries (256 / 8 bytes per entry), compact L0 after 2 files.
    private static PithosOptions Aggressive => new()
    {
        MemTableSizeThreshold    = 256,
        LevelZeroFileCountLimit  = 2,
        LevelSizeMultiplier      = 2,
    };

    private static byte[] K(int i) => BitConverter.GetBytes(i);
    private static byte[] V(int i) => BitConverter.GetBytes(i * 10);

    // Write enough entries to guarantee at least one L0 compaction.
    private static void FillDb(PithosDb db, int start = 0, int count = 200)
    {
        for (int i = start; i < start + count; i++)
            db.Put(K(i), V(i));
    }

    // ── Correctness ────────────────────────────────────────────────────────

    [Fact]
    public void Compaction_PreservesAllKeys()
    {
        using var db = new PithosDb(_dir, Aggressive);
        FillDb(db);

        for (int i = 0; i < 200; i++)
        {
            Assert.True(db.TryGet(K(i), out var value), $"key {i} missing after compaction");
            Assert.Equal(V(i), value);
        }
    }

    [Fact]
    public void Compaction_NewestValueWins_OnDuplicateKey()
    {
        using var db = new PithosDb(_dir, Aggressive);

        // Write key 1 and flush it into an SSTable.
        db.Put(K(1), V(1));
        FillDb(db, start: 100, count: 100);

        // Overwrite key 1 — it will land in a later SSTable.
        db.Put(K(1), BitConverter.GetBytes(9999));
        FillDb(db, start: 200, count: 100);

        Assert.True(db.TryGet(K(1), out var value));
        Assert.Equal(BitConverter.GetBytes(9999), value);
    }

    [Fact]
    public void Compaction_TombstoneEliminatesOlderValue()
    {
        using var db = new PithosDb(_dir, Aggressive);

        // Write key 42 and flush it.
        db.Put(K(42), V(42));
        FillDb(db, start: 100, count: 100);

        // Delete key 42 — tombstone flushes in a later SSTable.
        db.Delete(K(42));
        FillDb(db, start: 200, count: 100);

        Assert.False(db.TryGet(K(42), out _));
    }

    [Fact]
    public void Compaction_KeyAbsentAfterAllVersionsDeleted()
    {
        using var db = new PithosDb(_dir, Aggressive);

        // Multiple write/delete cycles across different SSTables.
        db.Put(K(7), V(7));
        FillDb(db, start: 100, count: 100);
        db.Put(K(7), V(77));
        FillDb(db, start: 200, count: 100);
        db.Delete(K(7));
        FillDb(db, start: 300, count: 100);

        Assert.False(db.TryGet(K(7), out _));
    }

    // ── Structural ─────────────────────────────────────────────────────────

    [Fact]
    public void Compaction_ReducesL0FileCount()
    {
        using var db = new PithosDb(_dir, Aggressive);
        FillDb(db);

        // Compaction is asynchronous — wait up to 5 s for L0 to drain.
        SpinWait.SpinUntil(
            () => Directory.GetFiles(_dir, "L0_*.sst").Length < Aggressive.LevelZeroFileCountLimit,
            TimeSpan.FromSeconds(5));

        var l0Count = Directory.GetFiles(_dir, "L0_*.sst").Length;
        Assert.True(l0Count < Aggressive.LevelZeroFileCountLimit,
            $"Expected L0 file count < {Aggressive.LevelZeroFileCountLimit}, got {l0Count}");
    }

    [Fact]
    public void Compaction_ProducesHigherLevelFiles()
    {
        using var db = new PithosDb(_dir, Aggressive);
        FillDb(db);

        // Compaction is asynchronous — wait up to 5 s for a higher-level file to appear.
        SpinWait.SpinUntil(
            () => Directory.GetFiles(_dir, "*.sst")
                           .Any(f => !Path.GetFileName(f).StartsWith("L0_", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        var higherLevel = Directory.GetFiles(_dir, "*.sst")
            .Where(f => !Path.GetFileName(f).StartsWith("L0_", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(higherLevel);
    }

    [Fact]
    public void Compaction_DeletesSourceFiles()
    {
        string[] sstsBefore;
        using (var db = new PithosDb(_dir, Aggressive))
        {
            FillDb(db);
            sstsBefore = Directory.GetFiles(_dir, "*.sst");
        }

        // After compaction at least some source SSTables should be gone.
        var sstsAfter = Directory.GetFiles(_dir, "*.sst");
        Assert.True(sstsAfter.Length < sstsBefore.Length || sstsAfter.Length <= 4,
            "Expected compaction to delete source SSTable files");
    }

    [Fact]
    public void CascadeCompaction_ProducesL2File()
    {
        // LevelSizeMultiplier=2: L0 limit=2, L1 limit=4.
        // Writing 1 600 entries (~50 flushes → 25 L0 compactions → 6+ L1 files) forces L1→L2.
        using var db = new PithosDb(_dir, Aggressive);
        FillDb(db, count: 1600);

        // Compaction is asynchronous — wait up to 10 s for cascade to reach L2.
        SpinWait.SpinUntil(
            () => Directory.GetFiles(_dir, "L2_*.sst").Length > 0,
            TimeSpan.FromSeconds(10));

        var l2Files = Directory.GetFiles(_dir, "L2_*.sst");
        Assert.NotEmpty(l2Files);
    }

    // ── MergeEntries deduplication ─────────────────────────────────────────

    [Fact]
    public void MergeEntries_DeduplicatesKey_WhenBothVersionsInSameCompactionBatch()
    {
        // threshold=512 (64 entries × 8 B each) and L0 limit=2 ensures:
        //   flush 1 → SSTable 0 (contains K(1)=V(1))
        //   flush 2 → SSTable 1 (contains K(1)=9999)  →  L0 now has 2 files → compact
        // Both versions of K(1) land in the same compaction, exercising the
        // MergeEntries deduplication branch (same key seen twice in the priority queue).
        var opts = new PithosOptions
        {
            MemTableSizeThreshold   = 512,
            LevelZeroFileCountLimit = 2,
            LevelSizeMultiplier     = 10,
        };
        using var db = new PithosDb(_dir, opts);

        // Batch 1: K(1) + 63 filler = 64 entries = 512 B → SSTable 0
        db.Put(K(1), V(1));
        for (int i = 100; i < 163; i++) db.Put(K(i), V(i));

        // Batch 2: K(1) overwrite + 63 filler = 64 entries → SSTable 1 → compaction
        db.Put(K(1), BitConverter.GetBytes(9999));
        for (int i = 200; i < 263; i++) db.Put(K(i), V(i));

        Assert.True(db.TryGet(K(1), out var value));
        Assert.Equal(BitConverter.GetBytes(9999), value);
    }

    // ── Durability ─────────────────────────────────────────────────────────

    [Fact]
    public void Compaction_DataAccessibleAfterReopen()
    {
        using (var db = new PithosDb(_dir, Aggressive))
            FillDb(db);

        using var db2 = new PithosDb(_dir, Aggressive);
        for (int i = 0; i < 200; i++)
        {
            Assert.True(db2.TryGet(K(i), out var value), $"key {i} missing after reopen");
            Assert.Equal(V(i), value);
        }
    }

    [Fact]
    public void Compaction_TombstoneRespectedAfterReopen()
    {
        using (var db = new PithosDb(_dir, Aggressive))
        {
            db.Put(K(42), V(42));
            FillDb(db, start: 100, count: 100);
            db.Delete(K(42));
            FillDb(db, start: 200, count: 100);
        }

        using var db2 = new PithosDb(_dir, Aggressive);
        Assert.False(db2.TryGet(K(42), out _));
    }
}
