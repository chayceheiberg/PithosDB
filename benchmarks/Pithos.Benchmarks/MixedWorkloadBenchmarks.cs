using BenchmarkDotNet.Attributes;
using Pithos.Core;

namespace Pithos.Benchmarks;

/// <summary>
/// Stress-tests ReaderWriterLockSlim contention by running concurrent reads
/// with and without an interleaved writer. The difference between
/// ConcurrentReadsOnly and ConcurrentReadWrite shows the cost of write-lock
/// contention blocking parallel readers.
/// </summary>
[MemoryDiagnoser]
public class MixedWorkloadBenchmarks
{
    private const int OperationsPerTask = 500;

    [Params(2, 8)]
    public int ReaderCount;

    private string _dir = null!;
    private PithosDb _db = null!;

    [IterationSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"pithos_mixed_{Guid.NewGuid():N}");
        _db = new PithosDb(_dir);
        for (int i = 0; i < 500; i++)
            _db.Put(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 2));
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Benchmark(Baseline = true)]
    public void ConcurrentReadsOnly()
    {
        var tasks = new Task[ReaderCount];
        for (int r = 0; r < ReaderCount; r++)
        {
            int seed = r;
            tasks[r] = Task.Run(() =>
            {
                var rng = new Random(seed);
                for (int i = 0; i < OperationsPerTask; i++)
                    _db.TryGet(BitConverter.GetBytes(rng.Next(500)), out _);
            });
        }
        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }

    [Benchmark]
    public void ConcurrentReadWrite()
    {
        var tasks = new Task[1 + ReaderCount];

        tasks[0] = Task.Run(() =>
        {
            for (int i = 0; i < OperationsPerTask; i++)
                _db.Put(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 2));
        });

        for (int r = 1; r <= ReaderCount; r++)
        {
            int seed = r;
            tasks[r] = Task.Run(() =>
            {
                var rng = new Random(seed);
                for (int i = 0; i < OperationsPerTask; i++)
                    _db.TryGet(BitConverter.GetBytes(rng.Next(500)), out _);
            });
        }

        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }
}
