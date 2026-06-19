using Pithos.Core;

namespace Pithos.Tests;

public class PithosOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PithosOptionsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Default_HasExpectedValues()
    {
        var opts = PithosOptions.Default;

        Assert.Equal(4 * 1024 * 1024, opts.MemTableSizeThreshold);
        Assert.Equal(0.01, opts.BloomFilterFalsePositiveRate);
        Assert.Equal(7, opts.LevelCount);
        Assert.Equal(10, opts.LevelZeroFileCountLimit);
        Assert.Equal(10, opts.LevelSizeMultiplier);
    }

    [Fact]
    public void NullOptions_UsesDefaults()
    {
        // Should not throw — null falls back to PithosOptions.Default.
        using var db = new PithosDb(_dir, null);
        db.Put("k"u8.ToArray(), "v"u8.ToArray());
        Assert.True(db.TryGet("k"u8.ToArray(), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidMemTableThreshold_ThrowsOnOpen(long threshold)
    {
        var opts = new PithosOptions { MemTableSizeThreshold = threshold };
        Assert.Throws<ArgumentOutOfRangeException>(() => new PithosDb(_dir, opts));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void InvalidBloomFPR_ThrowsOnOpen(double fpr)
    {
        var opts = new PithosOptions { BloomFilterFalsePositiveRate = fpr };
        Assert.Throws<ArgumentOutOfRangeException>(() => new PithosDb(_dir, opts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLevelCount_ThrowsOnOpen(int count)
    {
        var opts = new PithosOptions { LevelCount = count };
        Assert.Throws<ArgumentOutOfRangeException>(() => new PithosDb(_dir, opts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLevelZeroLimit_ThrowsOnOpen(int limit)
    {
        var opts = new PithosOptions { LevelZeroFileCountLimit = limit };
        Assert.Throws<ArgumentOutOfRangeException>(() => new PithosDb(_dir, opts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void InvalidLevelSizeMultiplier_ThrowsOnOpen(int multiplier)
    {
        var opts = new PithosOptions { LevelSizeMultiplier = multiplier };
        Assert.Throws<ArgumentOutOfRangeException>(() => new PithosDb(_dir, opts));
    }

    [Fact]
    public void CustomThreshold_FlushesAtConfiguredSize()
    {
        // Tiny threshold forces a flush after the very first write.
        var opts = new PithosOptions { MemTableSizeThreshold = 1 };
        using var db = new PithosDb(_dir, opts);

        db.Put("hello"u8.ToArray(), "world"u8.ToArray());

        Assert.True(db.TryGet("hello"u8.ToArray(), out var value));
        Assert.Equal("world"u8.ToArray(), value);
        Assert.NotEmpty(Directory.GetFiles(_dir, "*.sst"));
    }
}
