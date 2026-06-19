using BenchmarkDotNet.Attributes;
using Pithos.Core;

namespace Pithos.Benchmarks;

/// <summary>
/// Quantifies the overhead of L0 compaction by comparing total write time for
/// the same entry count under two configurations: one that never triggers
/// compaction (very high L0 limit) and one that triggers it aggressively
/// (LevelZeroFileCountLimit = 4).
///
/// With MemTableSizeThreshold = 1 KB and ~8 bytes per entry, each flush holds
/// roughly 128 entries. Writing 1 000 entries produces ~7–8 L0 files, which
/// triggers at least one compaction cascade in the aggressive config.
/// </summary>
[MemoryDiagnoser]
public class CompactionBenchmarks
{
    [Params(500, 1_000)]
    public int EntryCount;

    private static readonly PithosOptions NoCompactionOptions = new()
    {
        MemTableSizeThreshold = 1024,
        LevelZeroFileCountLimit = 100_000
    };

    private static readonly PithosOptions AggressiveOptions = new()
    {
        MemTableSizeThreshold = 1024,
        LevelZeroFileCountLimit = 4
    };

    private string _dir = null!;

    [IterationSetup]
    public void Setup() =>
        _dir = Path.Combine(Path.GetTempPath(), $"pithos_compact_{Guid.NewGuid():N}");

    [IterationCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [Benchmark(Baseline = true)]
    public void WritesWithoutCompaction()
    {
        using var db = new PithosDb(Path.Combine(_dir, "no_compact"), NoCompactionOptions);
        for (int i = 0; i < EntryCount; i++)
            db.Put(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 2));
    }

    [Benchmark]
    public void WritesWithCompaction()
    {
        using var db = new PithosDb(Path.Combine(_dir, "with_compact"), AggressiveOptions);
        for (int i = 0; i < EntryCount; i++)
            db.Put(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 2));
    }
}
