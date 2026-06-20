using PithosDB.Core;

namespace PithosDB.Tests;

/// <summary>A filter that drops any key whose UTF-8 string starts with a given prefix.</summary>
file sealed class PrefixDropFilter(string prefix) : ICompactionFilter
{
    private readonly byte[] _prefix = System.Text.Encoding.UTF8.GetBytes(prefix);

    public bool ShouldKeep(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        => !key.StartsWith(_prefix);
}

/// <summary>A filter that drops values that equal a specific byte sequence.</summary>
file sealed class ValueDropFilter(byte[] drop) : ICompactionFilter
{
    public bool ShouldKeep(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        => !value.SequenceEqual(drop);
}

public class CompactionFilterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public CompactionFilterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── Read-time filtering ────────────────────────────────────────────────

    [Fact]
    public void Filter_HidesKey_AtReadTime_WhenInMemTable()
    {
        var opts = new PithosOptions { CompactionFilter = new PrefixDropFilter("drop:") };
        using var db = new PithosDb(_dir, opts);

        db.Put("drop:foo"u8.ToArray(), "bar"u8.ToArray());
        db.Put("keep:foo"u8.ToArray(), "baz"u8.ToArray());

        Assert.False(db.TryGet("drop:foo"u8.ToArray(), out _));
        Assert.True(db.TryGet("keep:foo"u8.ToArray(), out _));
    }

    [Fact]
    public void Filter_HidesKey_AtReadTime_WhenInSSTable()
    {
        var opts = new PithosOptions
        {
            CompactionFilter = new PrefixDropFilter("drop:"),
            MemTableSizeThreshold = 256,
        };
        using var db = new PithosDb(_dir, opts);

        // Write enough to flush to SSTable.
        db.Put("drop:key"u8.ToArray(), new byte[128]);
        db.Put("keep:key"u8.ToArray(), new byte[128]);
        db.Put("padding__"u8.ToArray(), new byte[128]); // trigger flush

        Assert.False(db.TryGet("drop:key"u8.ToArray(), out _));
        Assert.True(db.TryGet("keep:key"u8.ToArray(), out _));
    }

    [Fact]
    public void Filter_ExcludesKey_FromScan()
    {
        var opts = new PithosOptions { CompactionFilter = new PrefixDropFilter("drop:") };
        using var db = new PithosDb(_dir, opts);

        db.Put("drop:a"u8.ToArray(), "1"u8.ToArray());
        db.Put("keep:b"u8.ToArray(), "2"u8.ToArray());
        db.Put("drop:c"u8.ToArray(), "3"u8.ToArray());

        var results = db.Scan().ToList();

        Assert.Single(results);
        Assert.Equal("keep:b"u8.ToArray(), results[0].key);
    }

    [Fact]
    public void Filter_CanInspectValue()
    {
        var sentinel = new byte[] { 0xFF, 0xDE, 0xAD };
        var opts = new PithosOptions { CompactionFilter = new ValueDropFilter(sentinel) };
        using var db = new PithosDb(_dir, opts);

        db.Put("a"u8.ToArray(), sentinel);
        db.Put("b"u8.ToArray(), "safe"u8.ToArray());

        Assert.False(db.TryGet("a"u8.ToArray(), out _));
        Assert.True(db.TryGet("b"u8.ToArray(), out _));
    }

    // ── Compaction-time physical removal ──────────────────────────────────

    [Fact]
    public void Filter_DropsEntry_DuringCompaction()
    {
        var opts = new PithosOptions
        {
            CompactionFilter      = new PrefixDropFilter("drop:"),
            MemTableSizeThreshold = 512,
            LevelZeroFileCountLimit = 2,
        };
        using var db = new PithosDb(_dir, opts);

        db.Put("drop:old"u8.ToArray(), new byte[256]);

        // Write more to trigger flush + compaction.
        for (int i = 0; i < 30; i++)
            db.Put(BitConverter.GetBytes(i), new byte[64]);

        // Filtered key should be gone at read time regardless.
        Assert.False(db.TryGet("drop:old"u8.ToArray(), out _));
    }

    // ── Filter + TTL together ─────────────────────────────────────────────

    [Fact]
    public void Filter_WorksWith_EnableTtl()
    {
        var opts = new PithosOptions
        {
            EnableTtl         = true,
            CompactionFilter  = new PrefixDropFilter("drop:"),
        };
        using var db = new PithosDb(_dir, opts);

        // TTL expiry should still hide this.
        db.Put("keep:expiring"u8.ToArray(), "v"u8.ToArray(), TimeSpan.FromMilliseconds(1));
        // Filter should hide this.
        db.Put("drop:live"u8.ToArray(), "v"u8.ToArray(), TimeSpan.FromSeconds(60));
        // Both active.
        db.Put("keep:live"u8.ToArray(), "v"u8.ToArray(), TimeSpan.FromSeconds(60));

        Thread.Sleep(20);

        Assert.False(db.TryGet("keep:expiring"u8.ToArray(), out _));
        Assert.False(db.TryGet("drop:live"u8.ToArray(), out _));
        Assert.True(db.TryGet("keep:live"u8.ToArray(), out _));
    }

    [Fact]
    public void Filter_ReceivesDecodedValue_WhenEnableTtl()
    {
        // Verify the filter sees the raw user value, not the TTL-encoded bytes.
        var sentinel = new byte[] { 0x42 };
        var opts = new PithosOptions
        {
            EnableTtl        = true,
            CompactionFilter = new ValueDropFilter(sentinel),
        };
        using var db = new PithosDb(_dir, opts);

        db.Put("a"u8.ToArray(), sentinel, TimeSpan.FromSeconds(60));
        db.Put("b"u8.ToArray(), new byte[] { 0x43 }, TimeSpan.FromSeconds(60));

        Assert.False(db.TryGet("a"u8.ToArray(), out _));
        Assert.True(db.TryGet("b"u8.ToArray(), out _));
    }
}
