using Pithos.Core;

namespace Pithos.Tests;

public class PithosDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PithosDbTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Put_ThenTryGet_ReturnsValue()
    {
        using var db = new PithosDb(_dir);
        db.Put(K("hello"), V("world"));

        Assert.True(db.TryGet(K("hello"), out var value));
        Assert.Equal(V("world"), value);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        using var db = new PithosDb(_dir);

        Assert.False(db.TryGet(K("missing"), out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Put_OverwritesExistingKey()
    {
        using var db = new PithosDb(_dir);
        db.Put(K("key"), V("first"));
        db.Put(K("key"), V("second"));

        Assert.True(db.TryGet(K("key"), out var value));
        Assert.Equal(V("second"), value);
    }

    [Fact]
    public void Delete_MakesKeyUnreadable()
    {
        using var db = new PithosDb(_dir);
        db.Put(K("key"), V("value"));
        db.Delete(K("key"));

        Assert.False(db.TryGet(K("key"), out _));
    }

    [Fact]
    public void Delete_NonExistentKey_DoesNotThrow()
    {
        using var db = new PithosDb(_dir);
        db.Delete(K("ghost")); // should not throw
        Assert.False(db.TryGet(K("ghost"), out _));
    }

    [Fact]
    public void WalRecovery_RestoresUnflushedWrites()
    {
        // Write without triggering a flush, then reopen.
        db_Put(K("persisted"), V("yes"));

        using var db2 = new PithosDb(_dir);
        Assert.True(db2.TryGet(K("persisted"), out var value));
        Assert.Equal(V("yes"), value);

        void db_Put(byte[] k, byte[] v)
        {
            using var db = new PithosDb(_dir);
            db.Put(k, v);
        }
    }

    [Fact]
    public void WalRecovery_DoesNotReturnDeletedKeys()
    {
        using (var db = new PithosDb(_dir))
        {
            db.Put(K("key"), V("value"));
            db.Delete(K("key"));
        }

        using var db2 = new PithosDb(_dir);
        Assert.False(db2.TryGet(K("key"), out _));
    }

    [Fact]
    public void SSTableRecovery_RestoresFlushedData()
    {
        // Force a flush by writing enough data to exceed the 4 MB threshold.
        using (var db = new PithosDb(_dir))
        {
            var bigValue = new byte[1024]; // 1 KB per entry
            for (int i = 0; i < 5000; i++)
                db.Put(K($"key-{i:D5}"), bigValue);
        }

        // Reopen — data must be recovered from SSTables, not the WAL.
        using var db2 = new PithosDb(_dir);
        Assert.True(db2.TryGet(K("key-00000"), out _));
        Assert.True(db2.TryGet(K("key-04999"), out _));
    }

    [Fact]
    public void SSTableRecovery_TombstoneRespected()
    {
        using (var db = new PithosDb(_dir))
        {
            var bigValue = new byte[1024];
            for (int i = 0; i < 5000; i++)
                db.Put(K($"key-{i:D5}"), bigValue);

            // Delete a key after the flush has happened (memtable tombstone).
            db.Delete(K("key-00000"));
        }

        using var db2 = new PithosDb(_dir);
        Assert.False(db2.TryGet(K("key-00000"), out _));
    }

    [Fact]
    public void MultipleWritesAndReads_Correct()
    {
        using var db = new PithosDb(_dir);
        var pairs = Enumerable.Range(0, 100)
            .Select(i => (key: K($"k{i}"), value: V($"v{i}")))
            .ToList();

        foreach (var (key, value) in pairs)
            db.Put(key, value);

        foreach (var (key, value) in pairs)
        {
            Assert.True(db.TryGet(key, out var result));
            Assert.Equal(value, result);
        }
    }
}
