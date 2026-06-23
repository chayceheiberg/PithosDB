using PithosDB.Core;

namespace PithosDB.Tests;

public class TransactionTests
{
    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ── Basic commit / rollback ───────────────────────────────────────────────

    [Fact]
    public void Transaction_Commit_WritesAreVisibleAfterCommit()
    {
        using var db = PithosDb.OpenInMemory();
        using var tx = db.BeginTransaction();
        tx.Put(K("k"), V("v"));
        tx.Commit();

        Assert.True(db.TryGet(K("k"), out var value));
        Assert.Equal(V("v"), value);
    }

    [Fact]
    public void Transaction_Rollback_WritesNotVisible()
    {
        using var db = PithosDb.OpenInMemory();
        using var tx = db.BeginTransaction();
        tx.Put(K("k"), V("v"));
        tx.Rollback();

        Assert.False(db.TryGet(K("k"), out _));
    }

    [Fact]
    public void Transaction_Dispose_WithoutCommit_ActsAsRollback()
    {
        using var db = PithosDb.OpenInMemory();

        var tx = db.BeginTransaction();
        tx.Put(K("k"), V("v"));
        tx.Dispose();

        Assert.False(db.TryGet(K("k"), out _));
    }

    // ── Read-your-own-writes ──────────────────────────────────────────────────

    [Fact]
    public void Transaction_TryGet_ReadYourOwnPuts()
    {
        using var db = PithosDb.OpenInMemory();
        using var tx = db.BeginTransaction();

        tx.Put(K("k"), V("v"));

        Assert.True(tx.TryGet(K("k"), out var value));
        Assert.Equal(V("v"), value);
    }

    [Fact]
    public void Transaction_TryGet_ReadYourOwnDeletes()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));

        using var tx = db.BeginTransaction();
        tx.Delete(K("k"));

        Assert.False(tx.TryGet(K("k"), out _));
    }

    [Fact]
    public void Transaction_TryGet_PutThenDeleteSameKey_SeesDeletion()
    {
        using var db = PithosDb.OpenInMemory();
        using var tx = db.BeginTransaction();

        tx.Put(K("k"), V("v"));
        tx.Delete(K("k"));

        Assert.False(tx.TryGet(K("k"), out _));
    }

    // ── Snapshot isolation ────────────────────────────────────────────────────

    [Fact]
    public void Transaction_TryGet_SeesSnapshotValue_NotConcurrentWrite()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("original"));

        using var tx = db.BeginTransaction();

        // Transaction reads "a" and sees the snapshot value.
        Assert.True(tx.TryGet(K("a"), out var seen));
        Assert.Equal(V("original"), seen);

        // External writer creates a completely different key — "a" unchanged.
        db.Put(K("unrelated"), V("x"));

        // Commit succeeds: only "a" is in the read set and it was not modified.
        tx.Put(K("mine"), V("y"));
        tx.Commit();

        Assert.True(db.TryGet(K("mine"), out _));
    }

    // ── Conflict detection ────────────────────────────────────────────────────

    [Fact]
    public void Transaction_Commit_ThrowsConflict_WhenReadKeyModified()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v1"));

        using var tx = db.BeginTransaction();
        tx.TryGet(K("k"), out _); // adds "k" to read set

        // Concurrent writer changes the key.
        db.Put(K("k"), V("v2"));

        Assert.Throws<TransactionConflictException>(() => tx.Commit());
    }

    [Fact]
    public void Transaction_Commit_ThrowsConflict_WhenReadKeyDeleted()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v1"));

        using var tx = db.BeginTransaction();
        tx.TryGet(K("k"), out _); // adds "k" to read set

        db.Delete(K("k"));

        Assert.Throws<TransactionConflictException>(() => tx.Commit());
    }

    [Fact]
    public void Transaction_Commit_ThrowsConflict_WhenAbsentKeyWrittenExternally()
    {
        using var db = PithosDb.OpenInMemory();
        using var tx = db.BeginTransaction();

        // Read a key that does not exist — adds "new" to read set with snapshot value null.
        tx.TryGet(K("new"), out _);

        // Concurrent writer creates the key.
        db.Put(K("new"), V("surprise"));

        Assert.Throws<TransactionConflictException>(() => tx.Commit());
    }

    [Fact]
    public void Transaction_Commit_NoConflict_WhenConcurrentWriteTouchesUnreadKey()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("va"));
        db.Put(K("b"), V("vb"));

        using var tx = db.BeginTransaction();
        tx.TryGet(K("a"), out _); // "a" in read set; "b" is NOT

        // Concurrent writer modifies only "b", which we never read.
        db.Put(K("b"), V("vb-updated"));

        // Should commit without conflict.
        tx.Put(K("c"), V("vc"));
        tx.Commit();

        Assert.True(db.TryGet(K("c"), out _));
    }

    // ── Read-modify-write ─────────────────────────────────────────────────────

    [Fact]
    public void Transaction_ReadModifyWrite_AppliesNewValue()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("counter"), V("10"));

        using var tx = db.BeginTransaction();
        tx.TryGet(K("counter"), out var current);
        tx.Put(K("counter"), V("11")); // simulate increment
        tx.Commit();

        Assert.True(db.TryGet(K("counter"), out var after));
        Assert.Equal(V("11"), after);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Transaction_Commit_Empty_Succeeds()
    {
        using var db = PithosDb.OpenInMemory();
        using var tx = db.BeginTransaction();
        tx.Commit(); // no reads, no writes — should succeed trivially
    }

    [Fact]
    public void Transaction_WriteOnly_NoConflict_Commits()
    {
        using var db = PithosDb.OpenInMemory();

        // Another writer active the whole time.
        db.Put(K("other"), V("x"));

        using var tx = db.BeginTransaction();
        tx.Put(K("mine"), V("y")); // no reads at all

        db.Put(K("other"), V("y")); // concurrent write — irrelevant, not in read set

        tx.Commit(); // succeeds: empty read set → nothing to conflict on
        Assert.True(db.TryGet(K("mine"), out _));
    }

    [Fact]
    public void Transaction_TryGet_AfterCommit_ThrowsObjectDisposedException()
    {
        using var db = PithosDb.OpenInMemory();
        var tx = db.BeginTransaction();
        tx.Commit();

        Assert.Throws<ObjectDisposedException>(() => tx.TryGet(K("k"), out _));
    }

    [Fact]
    public void Transaction_Commit_AfterCommit_ThrowsObjectDisposedException()
    {
        using var db = PithosDb.OpenInMemory();
        var tx = db.BeginTransaction();
        tx.Commit();

        Assert.Throws<ObjectDisposedException>(() => tx.Commit());
    }
}
