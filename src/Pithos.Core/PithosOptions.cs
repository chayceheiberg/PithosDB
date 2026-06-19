namespace Pithos.Core;

/// <summary>
/// Tuning options for <see cref="PithosDb"/>. All properties have sensible
/// defaults; only override what you need to change.
/// </summary>
public sealed class PithosOptions
{
    /// <summary>
    /// In-memory MemTable size in bytes at which a flush to an L0 SSTable is
    /// triggered. Larger values reduce write amplification but increase memory
    /// usage and recovery time. Default: 4 MB.
    /// </summary>
    public long MemTableSizeThreshold { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Target false positive probability for per-SSTable bloom filters (exclusive
    /// range 0–1). Lower values cut unnecessary disk reads but increase the
    /// in-memory and on-disk size of each filter. Default: 0.01 (1%).
    /// </summary>
    public double BloomFilterFalsePositiveRate { get; init; } = 0.01;

    /// <summary>
    /// Total number of compaction levels. Default: 7.
    /// </summary>
    public int LevelCount { get; init; } = 7;

    /// <summary>
    /// Maximum number of SSTable files allowed at L0 before a compaction into
    /// L1 is triggered. Default: 10.
    /// </summary>
    public int LevelZeroFileCountLimit { get; init; } = 10;

    /// <summary>
    /// Multiplier applied to the file-count limit at each successive level.
    /// With a limit of 10 and a multiplier of 10: L0=10, L1=100, L2=1 000, …
    /// Default: 10.
    /// </summary>
    public int LevelSizeMultiplier { get; init; } = 10;

    /// <summary>Default options — equivalent to <c>new PithosOptions()</c>.</summary>
    public static readonly PithosOptions Default = new();

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> if any property is outside
    /// its valid range.
    /// </summary>
    internal void Validate()
    {
        if (MemTableSizeThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(MemTableSizeThreshold), "Must be greater than zero.");

        if (BloomFilterFalsePositiveRate is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(BloomFilterFalsePositiveRate), "Must be between 0 and 1 exclusive.");

        if (LevelCount < 1)
            throw new ArgumentOutOfRangeException(nameof(LevelCount), "Must be at least 1.");

        if (LevelZeroFileCountLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(LevelZeroFileCountLimit), "Must be at least 1.");

        if (LevelSizeMultiplier < 2)
            throw new ArgumentOutOfRangeException(nameof(LevelSizeMultiplier), "Must be at least 2.");
    }
}
