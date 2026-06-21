<p align="center">
  <img src="pithos-logo.svg" alt="Pithos" width="160" />
</p>

# PithosDB

<p align="center">
  <a href="https://github.com/chayceheiberg/PithosDB/actions/workflows/ci.yml"><img src="https://github.com/chayceheiberg/PithosDB/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://codecov.io/gh/chayceheiberg/PithosDB"><img src="https://codecov.io/gh/chayceheiberg/PithosDB/branch/main/graph/badge.svg" alt="Coverage" /></a>
  <a href="https://www.nuget.org/packages/PithosDB"><img src="https://img.shields.io/nuget/v/PithosDB.svg" alt="NuGet" /></a>
  <a href="https://www.nuget.org/packages/PithosDB"><img src="https://img.shields.io/nuget/dt/PithosDB.svg" alt="NuGet Downloads" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT" /></a>
</p>

A persistent, embedded key-value storage engine built on an [LSM-tree](https://en.wikipedia.org/wiki/Log-structured_merge-tree) architecture. Written in C# targeting .NET 9.

**Docs:** [Architecture](docs/architecture.md) · [Storage Format](docs/storage-format.md)

---

## Architecture

```
Write path:   Put/Delete → WAL (disk) → MemTable (memory) → [flush] → L0 SSTable
Read path:    MemTable → L0 SSTables → L1 SSTables → ... → Ln SSTables
Scan path:    k-way merge across MemTable + all SSTable levels → sorted, deduplicated output
```

All public operations are **thread-safe**. Concurrent reads proceed in parallel via `ReaderWriterLockSlim`; writes take an exclusive lock.

---

## Usage

### Opening a Database

```csharp
using PithosDB.Core;

using var db = new PithosDb("path/to/data-directory");
```

The directory is created if it does not exist. On open, any unflushed WAL entries are replayed and existing SSTable files are recovered.

### In-Memory Mode

For testing or ephemeral workloads — no files are written to disk:

```csharp
using var db = PithosDb.OpenInMemory();
```

### Writing

```csharp
byte[] key   = Encoding.UTF8.GetBytes("hello");
byte[] value = Encoding.UTF8.GetBytes("world");

db.Put(key, value);
```

### Reading

```csharp
if (db.TryGet(key, out byte[]? value))
    Console.WriteLine(Encoding.UTF8.GetString(value!));
```

### Deleting

```csharp
db.Delete(key);
```

Tombstones are written immediately; physical removal happens during compaction.

### Atomic Write Batches

```csharp
var batch = new WriteBatch()
    .Put(Encoding.UTF8.GetBytes("user:1"), Encoding.UTF8.GetBytes("alice"))
    .Put(Encoding.UTF8.GetBytes("user:2"), Encoding.UTF8.GetBytes("bob"))
    .Delete(Encoding.UTF8.GetBytes("user:0"));

db.Write(batch);
```

The entire batch is written to the WAL as a single CRC32-guarded record — either all operations survive a crash or none do.

### Range Scanning

```csharp
// Bounded scan
foreach (var (key, value) in db.Scan(from: Encoding.UTF8.GetBytes("b"),
                                      to:   Encoding.UTF8.GetBytes("d")))
{
    Console.WriteLine($"{Encoding.UTF8.GetString(key)} = {Encoding.UTF8.GetString(value)}");
}

// Full scan
foreach (var (key, value) in db.Scan()) { ... }
```

### TTL (Time-To-Live)

```csharp
using var db = new PithosDb("path/to/data-directory", new PithosOptions { EnableTtl = true });

db.Put(key, value, TimeSpan.FromSeconds(30));

// TTL works in write batches too
var batch = new WriteBatch()
    .Put(sessionKey, sessionData, TimeSpan.FromHours(1))
    .Put(cacheKey,   cachedValue, TimeSpan.FromMinutes(5));
db.Write(batch);
```

> `EnableTtl` must be set consistently on every open of the same database. Toggling it on an existing database corrupts reads.

### Compaction Filter

```csharp
public sealed class PrefixFilter : ICompactionFilter
{
    public bool ShouldKeep(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        => !key.StartsWith("tmp:"u8);
}

using var db = new PithosDb("path/to/data-directory", new PithosOptions
{
    CompactionFilter = new PrefixFilter(),
});
```

Filtered entries are hidden at read time and physically removed on the next compaction.

### Async API

All operations have async counterparts that offload work to the thread pool, freeing the caller's thread during WAL fsyncs and SSTable reads:

```csharp
await db.PutAsync(key, value);
byte[]? value = await db.GetAsync(key);   // null = not found / deleted / expired
await db.DeleteAsync(key);
await db.WriteAsync(batch);

await foreach (var (key, value) in db.ScanAsync(from: start, to: end))
    Console.WriteLine(Encoding.UTF8.GetString(key));
```

---

## Configuration

```csharp
using var db = new PithosDb("path/to/data-directory", new PithosOptions
{
    MemTableSizeThreshold        = 8 * 1024 * 1024,
    BloomFilterFalsePositiveRate = 0.001,
    BlockCacheSizeBytes          = 32 * 1024 * 1024,
    BlockCacheKind               = BlockCacheKind.S3Fifo,
    Compression                  = CompressionKind.Lz4,
});
```

| Option | Default | Description |
|---|---|---|
| `MemTableSizeThreshold` | 4 MB | Raw data bytes before flushing to disk |
| `BloomFilterFalsePositiveRate` | 1% | Lower = fewer disk reads, larger filter |
| `LevelCount` | 7 | Total number of compaction levels |
| `LevelZeroFileCountLimit` | 10 | L0 file count that triggers compaction |
| `LevelSizeMultiplier` | 10 | File-count limit multiplier per level |
| `BlockCacheSizeBytes` | 8 MB | Max bytes for the shared block cache; set to 0 to disable |
| `BlockCacheKind` | `Lru` | Eviction policy: `Lru` or `S3Fifo` |
| `Compression` | `None` | Block compression: `None` or `Lz4` |
| `EnableTtl` | `false` | Per-entry TTL. Must be set consistently on every open |
| `CompactionFilter` | `null` | Optional `ICompactionFilter` applied at read time and compaction |
| `InMemory` | `false` | Store everything in memory; no files written to disk |

---

## Performance

Benchmarks run with [BenchmarkDotNet](https://benchmarkdotnet.org/) on .NET 9.0 · 12th Gen Intel Core i7-12700F.

### Writes

| Method | EntryCount | Mean | Allocated |
|---|---|---|---|
| `SequentialPuts` | 1,000 | 5.91 ms | 612 KB |
| `RandomPuts` | 1,000 | 5.79 ms | 612 KB |
| `SequentialPuts` | 10,000 | 90.4 ms | 8,728 KB |
| `RandomPuts` | 10,000 | 97.4 ms | 8,728 KB |

### Reads

| Method | Mean | Allocated |
|---|---|---|
| `MemTableHit` | 20.9 ns | 0 B |
| `SSTableHit` | 1,479 ns | 2,192 B |
| `KeyMiss` | 1,761 ns | 2,160 B |

### Block Cache (256 KB cache, 90/10 hot-key pattern)

| Method | Mean | Ratio | Allocated |
|---|---|---|---|
| `NoCache_HotRead` | 6,353 ns | 1.00 | 5,423 B |
| `Lru_HotRead` | 1,672 ns | 0.26 | 878 B |
| `S3Fifo_HotRead` | 1,663 ns | 0.26 | 878 B |
| `NoCache_Mixed` | 6,407 ns | 1.01 | 5,349 B |
| `Lru_Mixed` | 1,259 ns | 0.20 | 1,028 B |
| `S3Fifo_Mixed` | 1,149 ns | 0.18 | 1,022 B |

### Block Compression (cache disabled)

| Method | Mean | Ratio |
|---|---|---|
| `Read_None` | 2,684 ns | 1.00 |
| `Read_Lz4` | 2,769 ns | 1.03 |

LZ4 adds ~3% read latency; when the block cache is enabled the overhead is fully amortised after the first read.

### Running Benchmarks

```bash
dotnet run -c Release --project benchmarks/PithosDB.Benchmarks
dotnet run -c Release --project benchmarks/PithosDB.Benchmarks -- --filter "*ReadBenchmarks*"
```

---

## Building & Testing

```bash
dotnet build
dotnet test
```

Requires .NET 9 SDK.

---

## Contributing

Every push and pull request runs the full test suite via GitHub Actions. Releases are published to NuGet automatically when a version tag is pushed:

```bash
git tag v1.4.0
git push origin v1.4.0
```

The publish workflow packs both projects at the tagged version, pushes to NuGet.org, and creates a GitHub Release with auto-generated notes. A `NUGET_API_KEY` secret must be configured under **repo → Settings → Secrets and variables → Actions**.
