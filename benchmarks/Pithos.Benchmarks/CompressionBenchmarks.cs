using BenchmarkDotNet.Attributes;
using Pithos.Core;

namespace Pithos.Benchmarks;

/// <summary>
/// Compares point-lookup throughput with and without LZ4 block compression.
/// The block cache is disabled so every read decompresses from disk, making
/// the decompression cost visible. In practice (cache enabled) the overhead
/// is amortized — compressed blocks stay smaller in cache, improving hit rates.
/// </summary>
[MemoryDiagnoser]
public class CompressionBenchmarks
{
    private const int TotalEntries = 5_000;

    private static PithosOptions Opts(CompressionKind compression) => new()
    {
        MemTableSizeThreshold   = 32 * 1024,
        LevelZeroFileCountLimit = 100_000,
        BlockCacheSizeBytes     = 0, // disable cache — every read hits disk + decompresses
        Compression             = compression,
    };

    private string _dirNone = null!;
    private string _dirLz4  = null!;

    private PithosDb _dbNone = null!;
    private PithosDb _dbLz4  = null!;

    private byte[][] _keys = null!;
    private Random   _rng  = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rng  = new Random(42);
        _keys = Enumerable.Range(0, TotalEntries)
            .Select(i => BitConverter.GetBytes(i))
            .ToArray();

        _dirNone = Seed(Opts(CompressionKind.None));
        _dirLz4  = Seed(Opts(CompressionKind.Lz4));

        _dbNone = new PithosDb(_dirNone, Opts(CompressionKind.None));
        _dbLz4  = new PithosDb(_dirLz4,  Opts(CompressionKind.Lz4));
    }

    private static string Seed(PithosOptions opts)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pithos_comp_{Guid.NewGuid():N}");
        using var db = new PithosDb(dir, opts);
        var value = new byte[64];
        for (int i = 0; i < TotalEntries; i++)
            db.Put(BitConverter.GetBytes(i), value);
        return dir;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dbNone.Dispose(); Directory.Delete(_dirNone, recursive: true);
        _dbLz4.Dispose();  Directory.Delete(_dirLz4,  recursive: true);
    }

    [Benchmark(Baseline = true)]
    public bool Read_None() => _dbNone.TryGet(_keys[_rng.Next(TotalEntries)], out _);

    [Benchmark]
    public bool Read_Lz4() => _dbLz4.TryGet(_keys[_rng.Next(TotalEntries)], out _);
}
