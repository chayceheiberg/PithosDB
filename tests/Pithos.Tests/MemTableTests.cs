using Pithos.Core.Core;

namespace Pithos.Tests;

public class MemTableTests
{
    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Put_ThenTryGet_ReturnsValue()
    {
        var table = new MemTable();
        table.Put(K("key"), V("value"));

        Assert.True(table.TryGet(K("key"), out var result));
        Assert.Equal(V("value"), result);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var table = new MemTable();

        Assert.False(table.TryGet(K("missing"), out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Put_OverwritesExistingKey()
    {
        var table = new MemTable();
        table.Put(K("key"), V("first"));
        table.Put(K("key"), V("second"));

        Assert.True(table.TryGet(K("key"), out var result));
        Assert.Equal(V("second"), result);
    }

    [Fact]
    public void Delete_SetsTombstone()
    {
        var table = new MemTable();
        table.Put(K("key"), V("value"));
        table.Delete(K("key"));

        Assert.True(table.TryGet(K("key"), out var result));
        Assert.Null(result); // null = tombstone
    }

    [Fact]
    public void Delete_NonExistentKey_SetsTombstone()
    {
        var table = new MemTable();
        table.Delete(K("ghost"));

        Assert.True(table.TryGet(K("ghost"), out var result));
        Assert.Null(result);
    }

    [Fact]
    public void GetSortedEntries_ReturnsByteLexicographicOrder()
    {
        var table = new MemTable();
        table.Put(K("c"), V("3"));
        table.Put(K("a"), V("1"));
        table.Put(K("b"), V("2"));

        var keys = table.GetSortedEntries().Select(e => e.Key).ToList();

        Assert.Equal([K("a"), K("b"), K("c")], keys);
    }

    [Fact]
    public void SizeBytes_TracksWriteSize()
    {
        var table = new MemTable();
        Assert.Equal(0, table.SizeBytes);

        table.Put(K("key"), V("value"));
        Assert.True(table.SizeBytes > 0);
    }

    [Fact]
    public void SizeBytes_DecreasesOnOverwrite()
    {
        var table = new MemTable();
        table.Put(K("key"), V("large-value-here"));
        long before = table.SizeBytes;

        table.Put(K("key"), V("x"));
        Assert.True(table.SizeBytes < before);
    }

    [Fact]
    public void Clear_ResetsTableAndSize()
    {
        var table = new MemTable();
        table.Put(K("key"), V("value"));
        table.Clear();

        Assert.Equal(0, table.SizeBytes);
        Assert.False(table.TryGet(K("key"), out _));
    }
}
