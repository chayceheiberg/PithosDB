using Pithos.Core;
using Pithos.Core.Core;

namespace Pithos.Tests;

/// <summary>
/// Verifies CRC32 integrity for individual Put and Delete WAL records.
/// Batch-record CRC behaviour is covered in WriteBatchTests.
/// </summary>
public class WalChecksumTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public WalChecksumTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(int i) => BitConverter.GetBytes(i);
    private static byte[] V(int i) => BitConverter.GetBytes(i * 10);
    private string WalPath => Path.Combine(_dir, "wal.log");

    // ── Happy path ─────────────────────────────────────────────────────────

    [Fact]
    public void Replay_SinglePut_ReturnsEntry()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendPut(K(1), V(1));

        var entries = WriteAheadLog.Replay(WalPath).ToList();
        Assert.Single(entries);
        Assert.Equal(WalEntryType.Put, entries[0].type);
        Assert.Equal(K(1), entries[0].key);
        Assert.Equal(V(1), entries[0].value);
    }

    [Fact]
    public void Replay_SingleDelete_ReturnsEntry()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendDelete(K(7));

        var entries = WriteAheadLog.Replay(WalPath).ToList();
        Assert.Single(entries);
        Assert.Equal(WalEntryType.Delete, entries[0].type);
        Assert.Equal(K(7), entries[0].key);
        Assert.Null(entries[0].value);
    }

    [Fact]
    public void Replay_MixedRecords_ReturnsAllInOrder()
    {
        using (var wal = new WriteAheadLog(WalPath))
        {
            wal.AppendPut(K(1), V(1));
            wal.AppendDelete(K(2));
            wal.AppendPut(K(3), V(3));
        }

        var entries = WriteAheadLog.Replay(WalPath).ToList();
        Assert.Equal(3, entries.Count);
        Assert.Equal(WalEntryType.Put,    entries[0].type);
        Assert.Equal(WalEntryType.Delete, entries[1].type);
        Assert.Equal(WalEntryType.Put,    entries[2].type);
    }

    // ── Truncated CRC (simulates crash before CRC was fsynced) ────────────

    [Fact]
    public void Replay_TruncatedPutCrc_YieldsNothing()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendPut(K(1), V(1));

        // Remove the trailing 4-byte CRC so the record looks truncated.
        using (var fs = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite))
            fs.SetLength(fs.Length - 4);

        Assert.Empty(WriteAheadLog.Replay(WalPath));
    }

    [Fact]
    public void Replay_TruncatedDeleteCrc_YieldsNothing()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendDelete(K(1));

        using (var fs = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite))
            fs.SetLength(fs.Length - 4);

        Assert.Empty(WriteAheadLog.Replay(WalPath));
    }

    [Fact]
    public void Replay_TruncatedPutBody_YieldsNothing()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendPut(K(1), V(1));

        // Remove more bytes so even the value is partially missing.
        using (var fs = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite))
            fs.SetLength(fs.Length - 8);

        Assert.Empty(WriteAheadLog.Replay(WalPath));
    }

    // ── Corrupt CRC (simulates bit-flip in value or CRC field) ───────────

    [Fact]
    public void Replay_CorruptPutCrc_YieldsNothing()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendPut(K(1), V(1));

        var bytes = File.ReadAllBytes(WalPath);
        bytes[^1] ^= 0xFF; // flip last byte of CRC
        File.WriteAllBytes(WalPath, bytes);

        Assert.Empty(WriteAheadLog.Replay(WalPath));
    }

    [Fact]
    public void Replay_CorruptDeleteCrc_YieldsNothing()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendDelete(K(1));

        var bytes = File.ReadAllBytes(WalPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(WalPath, bytes);

        Assert.Empty(WriteAheadLog.Replay(WalPath));
    }

    [Fact]
    public void Replay_CorruptPutValue_YieldsNothing()
    {
        using (var wal = new WriteAheadLog(WalPath))
            wal.AppendPut(K(1), V(1));

        // Flip a bit in the value bytes (before the CRC) — CRC should catch it.
        var bytes = File.ReadAllBytes(WalPath);
        bytes[^5] ^= 0xFF; // 5th byte from end = first byte of value (value=4B, CRC=4B)
        File.WriteAllBytes(WalPath, bytes);

        Assert.Empty(WriteAheadLog.Replay(WalPath));
    }

    // ── Partial-write isolation ────────────────────────────────────────────

    [Fact]
    public void Replay_GoodRecordFollowedByCorruptRecord_ReturnsOnlyGoodOnes()
    {
        using (var wal = new WriteAheadLog(WalPath))
        {
            wal.AppendPut(K(1), V(1));
            wal.AppendPut(K(2), V(2));
        }

        // Corrupt the last 4 bytes — the CRC of the second record.
        var bytes = File.ReadAllBytes(WalPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(WalPath, bytes);

        var entries = WriteAheadLog.Replay(WalPath).ToList();
        Assert.Single(entries);
        Assert.Equal(K(1), entries[0].key);
    }

    [Fact]
    public void Replay_GoodRecordFollowedByTruncatedRecord_ReturnsOnlyGoodOnes()
    {
        using (var wal = new WriteAheadLog(WalPath))
        {
            wal.AppendDelete(K(10));
            wal.AppendPut(K(20), V(20));
        }

        // Truncate the CRC of the second record.
        using (var fs = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite))
            fs.SetLength(fs.Length - 4);

        var entries = WriteAheadLog.Replay(WalPath).ToList();
        Assert.Single(entries);
        Assert.Equal(WalEntryType.Delete, entries[0].type);
        Assert.Equal(K(10), entries[0].key);
    }

    // ── End-to-end durability ──────────────────────────────────────────────

    [Fact]
    public void PithosDb_Put_PersistsAfterReopen()
    {
        using (var db = new PithosDb(_dir))
            db.Put(K(42), V(42));

        using var db2 = new PithosDb(_dir);
        Assert.True(db2.TryGet(K(42), out var v));
        Assert.Equal(V(42), v);
    }

    [Fact]
    public void PithosDb_Delete_PersistsAfterReopen()
    {
        using (var db = new PithosDb(_dir))
        {
            db.Put(K(1), V(1));
            db.Delete(K(1));
        }

        using var db2 = new PithosDb(_dir);
        Assert.False(db2.TryGet(K(1), out _));
    }
}
