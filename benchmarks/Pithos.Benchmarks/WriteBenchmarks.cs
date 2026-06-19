using BenchmarkDotNet.Attributes;
using Pithos.Core;

namespace Pithos.Benchmarks;

/// <summary>
/// Measures raw write throughput for sequential vs. random key patterns.
/// Uses a small MemTableSizeThreshold so flushes are triggered during each
/// iteration, exercising the full MemTable → WAL → SSTable write path.
/// </summary>
[MemoryDiagnoser]
public class WriteBenchmarks
{
    [Params(1_000, 10_000)]
    public int EntryCount;

    private static readonly PithosOptions Options = new()
    {
        MemTableSizeThreshold = 4 * 1024
    };

    private string _dir = null!;
    private PithosDb _db = null!;

    [IterationSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"pithos_write_{Guid.NewGuid():N}");
        _db = new PithosDb(_dir, Options);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Benchmark]
    public void SequentialPuts()
    {
        for (int i = 0; i < EntryCount; i++)
            _db.Put(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 2));
    }

    [Benchmark]
    public void RandomPuts()
    {
        var rng = new Random(42);
        for (int i = 0; i < EntryCount; i++)
            _db.Put(BitConverter.GetBytes(rng.Next()), BitConverter.GetBytes(rng.Next()));
    }
}
