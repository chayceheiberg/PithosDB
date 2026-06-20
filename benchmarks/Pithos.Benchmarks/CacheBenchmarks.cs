using BenchmarkDotNet.Attributes;
using Pithos.Core;

namespace Pithos.Benchmarks;

/// <summary>
/// Compares LRU vs S3-FIFO block cache eviction policies under a skewed
/// (Zipfian-like) read workload designed to expose scan-pollution effects.
///
/// Setup: 5 000 entries are written across multiple SSTables. The cache is
/// intentionally small (256 KB) so it can only hold a fraction of the blocks
/// and eviction decisions matter. Reads then follow a 90/10 pattern: 90% of
/// accesses target the hot 10% of keys, with occasional sequential sweeps
/// ("scans") of the cold region to stress scan resistance.
/// </summary>
[MemoryDiagnoser]
public class CacheBenchmarks
{
    private const int TotalEntries  = 5_000;
    private const int HotKeys       = 500;           // top 10%
    private const int CacheSizeBytes = 256 * 1024;   // 256 KB — fits ~64 blocks

    private static PithosOptions Opts(BlockCacheKind? kind) => new()
    {
        MemTableSizeThreshold   = 32 * 1024,
        LevelZeroFileCountLimit = 100_000,
        BlockCacheSizeBytes     = kind is null ? 0 : CacheSizeBytes,
        BlockCacheKind          = kind ?? BlockCacheKind.Lru,
    };

    private string _dirNoCache  = null!;
    private string _dirLru      = null!;
    private string _dirS3Fifo  = null!;

    private PithosDb _dbNoCache = null!;
    private PithosDb _dbLru     = null!;
    private PithosDb _dbS3Fifo = null!;

    private byte[][] _hotReadKeys  = null!;
    private byte[][] _coldScanKeys = null!;
    private Random   _rng          = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rng = new Random(42);

        _hotReadKeys  = Enumerable.Range(0, HotKeys)
            .Select(i => BitConverter.GetBytes(i))
            .ToArray();
        _coldScanKeys = Enumerable.Range(HotKeys, TotalEntries - HotKeys)
            .Select(i => BitConverter.GetBytes(i))
            .ToArray();

        _dirNoCache = Seed(Opts(null));
        _dirLru     = Seed(Opts(BlockCacheKind.Lru));
        _dirS3Fifo  = Seed(Opts(BlockCacheKind.S3Fifo));

        _dbNoCache = new PithosDb(_dirNoCache, Opts(null));
        _dbLru     = new PithosDb(_dirLru,     Opts(BlockCacheKind.Lru));
        _dbS3Fifo  = new PithosDb(_dirS3Fifo,  Opts(BlockCacheKind.S3Fifo));
    }

    private static string Seed(PithosOptions opts)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pithos_cache_{Guid.NewGuid():N}");
        using var db = new PithosDb(dir, opts);
        var value = new byte[64]; // 64-byte values → ~9 entries per 4 KB block
        for (int i = 0; i < TotalEntries; i++)
            db.Put(BitConverter.GetBytes(i), value);
        return dir;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dbNoCache.Dispose(); Directory.Delete(_dirNoCache, recursive: true);
        _dbLru.Dispose();     Directory.Delete(_dirLru,     recursive: true);
        _dbS3Fifo.Dispose();  Directory.Delete(_dirS3Fifo,  recursive: true);
    }

    // ── Benchmarks ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hot read: always reads from the popular 10% of keys.
    /// Both caches should perform well here; establishes a baseline.
    /// </summary>
    [Benchmark(Baseline = true)]
    public bool NoCache_HotRead() => HotRead(_dbNoCache);

    [Benchmark]
    public bool Lru_HotRead() => HotRead(_dbLru);

    [Benchmark]
    public bool S3Fifo_HotRead() => HotRead(_dbS3Fifo);

    /// <summary>
    /// Mixed workload: 90% hot reads + 10% sequential cold scan.
    /// The cold scan pollutes LRU by evicting hot blocks; S3-FIFO's probation
    /// queue absorbs scan traffic without displacing the hot set from Main.
    /// </summary>
    [Benchmark]
    public bool NoCache_Mixed() => MixedRead(_dbNoCache);

    [Benchmark]
    public bool Lru_Mixed() => MixedRead(_dbLru);

    [Benchmark]
    public bool S3Fifo_Mixed() => MixedRead(_dbS3Fifo);

    // ── Helpers ────────────────────────────────────────────────────────────

    private bool HotRead(PithosDb db)
    {
        var key = _hotReadKeys[_rng.Next(HotKeys)];
        return db.TryGet(key, out _);
    }

    private bool MixedRead(PithosDb db)
    {
        if (_rng.NextDouble() < 0.10)
        {
            // Cold scan: read a random key from the cold region.
            var key = _coldScanKeys[_rng.Next(_coldScanKeys.Length)];
            return db.TryGet(key, out _);
        }
        return HotRead(db);
    }
}
