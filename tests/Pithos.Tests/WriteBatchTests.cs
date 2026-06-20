using Pithos.Core;
using Pithos.Core.Core;

namespace Pithos.Tests;

public class WriteBatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public WriteBatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(int i) => BitConverter.GetBytes(i);
    private static byte[] V(int i) => BitConverter.GetBytes(i * 10);
    private static string WalPath(string dir) => Path.Combine(dir, "wal.log");

    // ── WriteBatch API ─────────────────────────────────────────────────────

    [Fact]
    public void EmptyBatch_IsNoOp()
    {
        using var db = new PithosDb(_dir);
        db.Write(new WriteBatch()); // must not throw
        Assert.False(db.TryGet(K(1), out _));
    }

    [Fact]
    public void Batch_AllKeysReadable_AfterWrite()
    {
        using var db = new PithosDb(_dir);
        db.Write(new WriteBatch()
            .Put(K(1), V(1))
            .Put(K(2), V(2))
            .Put(K(3), V(3)));

        Assert.True(db.TryGet(K(1), out var v1)); Assert.Equal(V(1), v1);
        Assert.True(db.TryGet(K(2), out var v2)); Assert.Equal(V(2), v2);
        Assert.True(db.TryGet(K(3), out var v3)); Assert.Equal(V(3), v3);
    }

    [Fact]
    public void Batch_WithDelete_KeyAbsent()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(42), V(42));

        db.Write(new WriteBatch()
            .Put(K(99), V(99))
            .Delete(K(42)));

        Assert.True(db.TryGet(K(99), out _));
        Assert.False(db.TryGet(K(42), out _));
    }

    [Fact]
    public void Batch_LastPutWins_WhenSameKeyWrittenTwice()
    {
        using var db = new PithosDb(_dir);
        var first  = BitConverter.GetBytes(111);
        var second = BitConverter.GetBytes(222);

        db.Write(new WriteBatch()
            .Put(K(1), first)
            .Put(K(1), second));

        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(second, v);
    }

    [Fact]
    public void Batch_OverridesExistingKey()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(7), V(7));

        db.Write(new WriteBatch().Put(K(7), V(77)));

        Assert.True(db.TryGet(K(7), out var v));
        Assert.Equal(V(77), v);
    }

    // ── Durability via WAL Replay ──────────────────────────────────────────

    [Fact]
    public void Batch_PersistsAfterReopen()
    {
        using (var db = new PithosDb(_dir))
            db.Write(new WriteBatch().Put(K(1), V(1)).Put(K(2), V(2)));

        using var db2 = new PithosDb(_dir);
        Assert.True(db2.TryGet(K(1), out var v1)); Assert.Equal(V(1), v1);
        Assert.True(db2.TryGet(K(2), out var v2)); Assert.Equal(V(2), v2);
    }

    [Fact]
    public void Batch_DeletePersistsAfterReopen()
    {
        using (var db = new PithosDb(_dir))
        {
            db.Put(K(5), V(5));
            db.Write(new WriteBatch().Delete(K(5)));
        }

        using var db2 = new PithosDb(_dir);
        Assert.False(db2.TryGet(K(5), out _));
    }

    // ── WAL atomicity ──────────────────────────────────────────────────────

    [Fact]
    public void WAL_Replay_BatchWithValidCrc_AppliesAllOps()
    {
        // Write a single Put followed by a batch; all three ops should survive replay.
        using (var db = new PithosDb(_dir))
        {
            db.Put(K(1), V(1));
            db.Write(new WriteBatch().Put(K(2), V(2)).Put(K(3), V(3)));
        }

        var entries = WriteAheadLog.Replay(WalPath(_dir)).ToList();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.key.SequenceEqual(K(1)));
        Assert.Contains(entries, e => e.key.SequenceEqual(K(2)));
        Assert.Contains(entries, e => e.key.SequenceEqual(K(3)));
    }

    [Fact]
    public void WAL_Replay_TruncatedBatchCrc_StopsBeforeCorruptEntry()
    {
        // Simulate a crash that truncated the batch's CRC before it was written.
        using (var db = new PithosDb(_dir))
        {
            db.Put(K(1), V(1));
            db.Write(new WriteBatch().Put(K(2), V(2)));
        }

        // Remove the last 4 bytes (the CRC32) to simulate a partial write.
        using (var fs = new FileStream(WalPath(_dir), FileMode.Open, FileAccess.ReadWrite))
            fs.SetLength(fs.Length - 4);

        var entries = WriteAheadLog.Replay(WalPath(_dir)).ToList();
        Assert.Single(entries);
        Assert.True(entries[0].key.SequenceEqual(K(1)));
    }

    [Fact]
    public void WAL_Replay_CorruptBatchCrc_StopsBeforeCorruptEntry()
    {
        // Simulate bit-flipped CRC — batch is silently dropped, Put before it is kept.
        using (var db = new PithosDb(_dir))
        {
            db.Put(K(1), V(1));
            db.Write(new WriteBatch().Put(K(2), V(2)));
        }

        var bytes = File.ReadAllBytes(WalPath(_dir));
        bytes[^1] ^= 0xFF; // flip the last byte of the CRC
        File.WriteAllBytes(WalPath(_dir), bytes);

        var entries = WriteAheadLog.Replay(WalPath(_dir)).ToList();
        Assert.Single(entries);
        Assert.True(entries[0].key.SequenceEqual(K(1)));
    }
}
