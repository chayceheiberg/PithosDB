using BenchmarkDotNet.Attributes;
using Pithos.Core;

namespace Pithos.Benchmarks;

/// <summary>
/// Compares TryGet latency across three scenarios:
/// - MemTableHit: key is still in the in-memory buffer (no flush has occurred)
/// - SSTableHit: key was flushed to an L0 SSTable before the benchmark runs
/// - KeyMiss: key was never written; exercises the full search path + bloom filters
/// </summary>
[MemoryDiagnoser]
public class ReadBenchmarks
{
    private static readonly byte[] TargetKey = BitConverter.GetBytes(42);
    private static readonly byte[] TargetValue = BitConverter.GetBytes(999);
    private static readonly byte[] AbsentKey = BitConverter.GetBytes(int.MaxValue);

    // Small threshold so filler writes push TargetKey into an SSTable during setup.
    // High LevelZeroFileCountLimit avoids compaction noise during reads.
    private static readonly PithosOptions ColdOptions = new()
    {
        MemTableSizeThreshold = 512,
        LevelZeroFileCountLimit = 100_000
    };

    private string _hotDir = null!;
    private string _coldDir = null!;
    private PithosDb _hotDb = null!;
    private PithosDb _coldDb = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Hot DB: only TargetKey written — well within the threshold, so no flush occurs.
        _hotDir = Path.Combine(Path.GetTempPath(), $"pithos_hot_{Guid.NewGuid():N}");
        _hotDb = new PithosDb(_hotDir, PithosOptions.Default);
        _hotDb.Put(TargetKey, TargetValue);

        // Cold DB: write TargetKey then flood with filler until the MemTable flushes.
        // After the flush TargetKey lives in an L0 SSTable; the fresh MemTable does not
        // contain it, so TryGet must descend into on-disk storage.
        _coldDir = Path.Combine(Path.GetTempPath(), $"pithos_cold_{Guid.NewGuid():N}");
        _coldDb = new PithosDb(_coldDir, ColdOptions);
        _coldDb.Put(TargetKey, TargetValue);
        for (int i = 100; i < 2_000; i++)
            _coldDb.Put(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 2));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hotDb.Dispose();
        _coldDb.Dispose();
        Directory.Delete(_hotDir, recursive: true);
        Directory.Delete(_coldDir, recursive: true);
    }

    [Benchmark]
    public bool MemTableHit() => _hotDb.TryGet(TargetKey, out _);

    [Benchmark]
    public bool SSTableHit() => _coldDb.TryGet(TargetKey, out _);

    [Benchmark]
    public bool KeyMiss() => _coldDb.TryGet(AbsentKey, out _);
}
