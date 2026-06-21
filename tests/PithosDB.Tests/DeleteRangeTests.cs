using PithosDB.Core;

namespace PithosDB.Tests;

public class DeleteRangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public DeleteRangeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(int i) => BitConverter.GetBytes(i);
    private static byte[] V(int i) => BitConverter.GetBytes(i * 10);

    private PithosDb OpenDb() => new(_dir);

    // ── Range deletion ────────────────────────────────────────────────────

    [Fact]
    public void DeleteRange_RemovesKeysInRange()
    {
        using var db = OpenDb();
        for (int i = 0; i < 10; i++) db.Put(K(i), V(i));

        db.DeleteRange(K(3), K(6));

        for (int i = 0; i < 3; i++)  Assert.True(db.TryGet(K(i), out _), $"key {i} should survive");
        for (int i = 3; i <= 6; i++) Assert.False(db.TryGet(K(i), out _), $"key {i} should be deleted");
        for (int i = 7; i < 10; i++) Assert.True(db.TryGet(K(i), out _), $"key {i} should survive");
    }

    [Fact]
    public void DeleteRange_LeavesKeysOutsideRange_Intact()
    {
        using var db = OpenDb();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        db.DeleteRange(K(2), K(2)); // delete only key 2

        Assert.True(db.TryGet(K(1), out _));
        Assert.False(db.TryGet(K(2), out _));
        Assert.True(db.TryGet(K(3), out _));
    }

    [Fact]
    public void DeleteRange_EmptyRange_DoesNothing()
    {
        using var db = OpenDb();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        // from > to produces no matches from Scan — nothing deleted
        db.DeleteRange(K(9), K(0));

        for (int i = 0; i < 5; i++) Assert.True(db.TryGet(K(i), out _));
    }

    [Fact]
    public void DeleteRange_NoBounds_DeletesAll()
    {
        using var db = OpenDb();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        db.DeleteRange();

        for (int i = 0; i < 5; i++) Assert.False(db.TryGet(K(i), out _));
    }

    [Fact]
    public void DeleteRange_OpenFrom_DeletesFromStart()
    {
        using var db = OpenDb();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        db.DeleteRange(to: K(2));

        for (int i = 0; i <= 2; i++) Assert.False(db.TryGet(K(i), out _), $"key {i} should be deleted");
        for (int i = 3; i < 5; i++)  Assert.True(db.TryGet(K(i), out _),  $"key {i} should survive");
    }

    [Fact]
    public void DeleteRange_OpenTo_DeletesToEnd()
    {
        using var db = OpenDb();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        db.DeleteRange(from: K(3));

        for (int i = 0; i < 3; i++)  Assert.True(db.TryGet(K(i), out _),  $"key {i} should survive");
        for (int i = 3; i < 5; i++) Assert.False(db.TryGet(K(i), out _), $"key {i} should be deleted");
    }

    [Fact]
    public void DeleteRange_NonExistentRange_DoesNothing()
    {
        using var db = OpenDb();
        for (int i = 0; i < 3; i++) db.Put(K(i), V(i));

        db.DeleteRange(K(100), K(200)); // no keys exist here

        for (int i = 0; i < 3; i++) Assert.True(db.TryGet(K(i), out _));
    }

    // ── Durability ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteRange_PersistsAfterReopen()
    {
        using (var db = OpenDb())
        {
            for (int i = 0; i < 10; i++) db.Put(K(i), V(i));
            db.DeleteRange(K(3), K(7));
        }

        using var db2 = OpenDb();
        for (int i = 0; i < 3; i++)  Assert.True(db2.TryGet(K(i), out _),  $"key {i} should survive reopen");
        for (int i = 3; i <= 7; i++) Assert.False(db2.TryGet(K(i), out _), $"key {i} should be gone after reopen");
        for (int i = 8; i < 10; i++) Assert.True(db2.TryGet(K(i), out _),  $"key {i} should survive reopen");
    }

    // ── In-memory mode ────────────────────────────────────────────────────

    [Fact]
    public void DeleteRange_InMemory_Works()
    {
        using var db = PithosDb.OpenInMemory();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        db.DeleteRange(K(1), K(3));

        Assert.True(db.TryGet(K(0), out _));
        Assert.False(db.TryGet(K(1), out _));
        Assert.False(db.TryGet(K(2), out _));
        Assert.False(db.TryGet(K(3), out _));
        Assert.True(db.TryGet(K(4), out _));
    }

    // ── Async ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRangeAsync_RemovesKeysInRange()
    {
        using var db = OpenDb();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        await db.DeleteRangeAsync(K(1), K(3));

        Assert.True(db.TryGet(K(0), out _));
        Assert.False(db.TryGet(K(1), out _));
        Assert.False(db.TryGet(K(2), out _));
        Assert.False(db.TryGet(K(3), out _));
        Assert.True(db.TryGet(K(4), out _));
    }

    [Fact]
    public async Task DeleteRangeAsync_NoBounds_DeletesAll()
    {
        using var db = OpenDb();
        for (int i = 0; i < 5; i++) db.Put(K(i), V(i));

        await db.DeleteRangeAsync();

        for (int i = 0; i < 5; i++) Assert.False(db.TryGet(K(i), out _));
    }
}
