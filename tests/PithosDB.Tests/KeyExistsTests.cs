using PithosDB.Core;

namespace PithosDB.Tests;

public class KeyExistsTests
{
    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void KeyExists_PresentKey_ReturnsTrue()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        Assert.True(db.KeyExists(K("a")));
    }

    [Fact]
    public void KeyExists_MissingKey_ReturnsFalse()
    {
        using var db = PithosDb.OpenInMemory();
        Assert.False(db.KeyExists(K("missing")));
    }

    [Fact]
    public void KeyExists_DeletedKey_ReturnsFalse()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));
        db.Delete(K("k"));
        Assert.False(db.KeyExists(K("k")));
    }

    [Fact]
    public void KeyExists_MultipleKeys_CorrectPerKey()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));

        Assert.True(db.KeyExists(K("a")));
        Assert.True(db.KeyExists(K("b")));
        Assert.True(db.KeyExists(K("c")));
        Assert.False(db.KeyExists(K("d")));
    }

    [Fact]
    public void KeyExists_OverwrittenKey_ReturnsTrue()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v1"));
        db.Put(K("k"), V("v2"));
        Assert.True(db.KeyExists(K("k")));
    }

    [Fact]
    public void KeyExists_ExpiredTtl_ReturnsFalse()
    {
        using var db = PithosDb.OpenInMemory(new PithosOptions { InMemory = true, EnableTtl = true });
        db.Put(K("ttl"), V("val"), TimeSpan.FromMilliseconds(-1));
        Assert.False(db.KeyExists(K("ttl")));
    }

    [Fact]
    public void KeyExists_LiveTtl_ReturnsTrue()
    {
        using var db = PithosDb.OpenInMemory(new PithosOptions { InMemory = true, EnableTtl = true });
        db.Put(K("ttl"), V("val"), TimeSpan.FromHours(1));
        Assert.True(db.KeyExists(K("ttl")));
    }

    [Fact]
    public async Task KeyExistsAsync_PresentKey_ReturnsTrue()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("async-key"), V("value"));
        Assert.True(await db.KeyExistsAsync(K("async-key")));
    }

    [Fact]
    public async Task KeyExistsAsync_MissingKey_ReturnsFalse()
    {
        using var db = PithosDb.OpenInMemory();
        Assert.False(await db.KeyExistsAsync(K("nope")));
    }

    [Fact]
    public async Task KeyExistsAsync_CancellationToken_Cancels()
    {
        using var db = PithosDb.OpenInMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => db.KeyExistsAsync(K("k"), cts.Token));
    }
}
