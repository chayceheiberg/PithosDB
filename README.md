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

A persistent, embedded key-value storage engine built on an [LSM-tree](https://en.wikipedia.org/wiki/Log-structured_merge-tree) (Log-Structured Merge-Tree) architecture. Written in C# targeting .NET 9.

---

## Architecture

Pithos follows the standard LSM-tree design: all writes go to an in-memory buffer and an append-only log, then are periodically flushed to disk as immutable sorted files. Reads check each layer in order from newest to oldest.

```
Write path:   Put/Delete → WAL (disk) → MemTable (memory) → [flush] → L0 SSTable
Read path:    MemTable → L0 SSTables → L1 SSTables → ... → Ln SSTables
Scan path:    k-way merge across MemTable + all SSTable levels → sorted, deduplicated output
```

All public operations are **thread-safe**. Concurrent reads proceed in parallel via `ReaderWriterLockSlim`; writes take an exclusive lock.

### Components

#### MemTable (`Core/MemTable.cs`)
An in-memory `SortedDictionary<byte[], byte[]?>` that holds the most recent writes. Deleted keys are represented as tombstones (`null` values). When the MemTable exceeds the configured size threshold (default **4 MB**), it is flushed to an L0 SSTable on disk.

#### Write-Ahead Log (`Core/WriteAheadLog.cs`)
An append-only binary log written to `wal.log` in the database directory. Every `Put`, `Delete`, and `WriteBatch` is durably fsynced here before being applied to the MemTable. On startup, the WAL is replayed to restore any unflushed writes. The log is deleted and recreated after each successful flush.

Batch entries are written as a single CRC32-guarded record — either the entire batch survives a crash or none of it does. Individual `Put` and `Delete` records are also CRC32-guarded; a truncated or corrupt record causes replay to stop at that point, discarding only the partial write.

#### SSTable (`Storage/SSTableWriter.cs`, `Storage/SSTableReader.cs`)
Immutable, sorted files written when the MemTable is flushed. Each file has the following layout:

```
┌─────────────────────────────────────────────┐
│  Data blocks (≤4 KB each)                   │
│    [compression: 1 byte]                    │  ← 0 = none, 1 = LZ4
│    [payload length: 4 bytes]                │
│    [payload: compressed or raw block data]  │
│    [CRC32: 4 bytes]                         │  ← over compression byte + length + payload
├─────────────────────────────────────────────┤
│  Bloom filter                               │  ← MurmurHash3, configurable FPR (default 1%)
├─────────────────────────────────────────────┤
│  Sparse index                               │  ← first key + offset per block
├─────────────────────────────────────────────┤
│  Footer (16 bytes)                          │  ← bloomOffset (8) + indexOffset (8)
└─────────────────────────────────────────────┘
```

Each data block carries a CRC32 checksum (`System.IO.Hashing.Crc32`) over the compression byte, payload length, and payload. The checksum is verified on every block read from disk; a mismatch throws `InvalidDataException` before any data is returned.

Point lookups consult the bloom filter first; a definite miss skips all block I/O for that file. Range scans use the sparse index to seek directly to the first candidate block.

#### Block Compression
SSTable blocks can be compressed with **LZ4** (`K4os.Compression.LZ4`) before being written. Set `Compression = CompressionKind.Lz4` on `PithosOptions` to enable it.

The compression byte stored in each block header allows the reader to handle blocks transparently regardless of how they were written. The block cache stores **decompressed** bytes, so each block is decompressed at most once per cache warm-up cycle.

Benchmarks show LZ4 adds only **~3% read latency overhead** on cache-cold reads while meaningfully reducing SSTable file sizes for compressible data.

#### Bloom Filter (`Core/BloomFilter.cs`)
A bit-array bloom filter using double-hashing over MurmurHash3. Built per SSTable with a configurable false positive rate (default **1%**). Serialized into the SSTable file and loaded into memory on open. A definite miss means no disk reads are needed for that file.

#### Block Cache (`Core/LruBlockCache.cs`, `Core/S3FifoBlockCache.cs`)
A shared block cache reduces repeated disk reads for hot blocks. Both implementations satisfy the `IBlockCache` interface and are selected via `BlockCacheKind` on `PithosOptions`.

- **`LruBlockCache`** — Least-Recently-Used eviction. Good general-purpose default. Implemented as a `LinkedList` + `Dictionary` with byte-capacity eviction.
- **`S3FifoBlockCache`** — S3-FIFO eviction policy (SOSP 2023). New blocks enter a small probation queue (~10% of capacity); blocks accessed more than once are promoted to the main queue (~90%). A ghost set of recently-evicted keys lets re-admitted blocks skip probation. Provides better scan-pollution resistance than LRU with lower per-hit overhead (no structural mutation on cache hit).

Blocks are cached after the first disk read and checksum verification. Cache entries for a compacted file are evicted before the file is deleted. Enabled by default (8 MB LRU). Set `BlockCacheSizeBytes = 0` to disable.

#### Leveled Compactor (`Compaction/LeveledCompactor.cs`)
Runs a configurable leveled compaction strategy (default **7 levels**, **10× size multiplier**). When a level reaches its file-count limit, all its SSTables are merged into a single SSTable at the next level using a k-way merge (via `PriorityQueue`) that deduplicates keys, keeping the value from the newest source file.

---

## Project Structure

```
src/
└── PithosDB.Core/
    ├── PithosDb.cs                  # Public API / orchestration
    ├── PithosOptions.cs             # Runtime configuration (CompressionKind, BlockCacheKind)
    ├── WriteBatch.cs                # Atomic multi-key write batch
    ├── Core/
    │   ├── MemTable.cs
    │   ├── WriteAheadLog.cs
    │   ├── BloomFilter.cs
    │   ├── IBlockCache.cs           # Block cache interface
    │   ├── LruBlockCache.cs         # LRU eviction policy
    │   ├── S3FifoBlockCache.cs      # S3-FIFO eviction policy (SOSP 2023)
    │   └── ByteArrayComparer.cs
    ├── Storage/
    │   ├── SSTableWriter.cs
    │   └── SSTableReader.cs
    └── Compaction/
        └── LeveledCompactor.cs
tests/
└── PithosDB.Tests/                    # xUnit test project
benchmarks/
└── PithosDB.Benchmarks/               # BenchmarkDotNet performance tests
```

---

## Usage

### Opening a Database

```csharp
using PithosDB.Core;

using var db = new PithosDb("path/to/data-directory");
```

The directory is created if it does not exist. On open, any unflushed WAL entries are replayed and existing SSTable files are recovered into the level structure.

### Writing

```csharp
byte[] key   = Encoding.UTF8.GetBytes("hello");
byte[] value = Encoding.UTF8.GetBytes("world");

db.Put(key, value);
```

### Reading

```csharp
if (db.TryGet(key, out byte[]? value))
{
    Console.WriteLine(Encoding.UTF8.GetString(value!));
}
else
{
    Console.WriteLine("Key not found.");
}
```

### Deleting

```csharp
db.Delete(key);
```

Deletes are tombstoned — the key is logically removed and `TryGet` returns `false`. Tombstones are physically removed during compaction.

### Atomic Write Batches

`WriteBatch` applies multiple puts and deletes atomically. The entire batch is written to the WAL as a single CRC32-guarded record — either all operations are replayed on recovery or none are.

```csharp
var batch = new WriteBatch()
    .Put(Encoding.UTF8.GetBytes("user:1"), Encoding.UTF8.GetBytes("alice"))
    .Put(Encoding.UTF8.GetBytes("user:2"), Encoding.UTF8.GetBytes("bob"))
    .Delete(Encoding.UTF8.GetBytes("user:0"));

db.Write(batch);
```

Batches are not limited in size, but large batches delay the next MemTable flush check until after all operations are applied.

### Range Scanning

`Scan` returns all live entries within an inclusive key range, in sorted order. Either bound can be omitted for an open-ended scan.

```csharp
// Bounded scan
foreach (var (key, value) in db.Scan(from: Encoding.UTF8.GetBytes("b"),
                                       to:   Encoding.UTF8.GetBytes("d")))
{
    Console.WriteLine($"{Encoding.UTF8.GetString(key)} = {Encoding.UTF8.GetString(value)}");
}

// From a lower bound to the end
foreach (var (key, value) in db.Scan(from: Encoding.UTF8.GetBytes("m"))) { ... }

// Full scan
foreach (var (key, value) in db.Scan()) { ... }
```

Deleted keys are excluded from scan results. The scan reflects a consistent point-in-time snapshot across the MemTable and all SSTable levels.

### TTL (Time-To-Live)

Enable per-entry expiry by setting `EnableTtl = true` on `PithosOptions`. Expired entries are hidden at read time and physically removed during compaction.

```csharp
using var db = new PithosDb("path/to/data-directory", new PithosOptions { EnableTtl = true });

// Expires in 30 seconds
db.Put(key, value, TimeSpan.FromSeconds(30));

// TTL works in write batches too
var batch = new WriteBatch()
    .Put(sessionKey, sessionData, TimeSpan.FromHours(1))
    .Put(cacheKey,   cachedValue, TimeSpan.FromMinutes(5));
db.Write(batch);
```

> `EnableTtl` must be set consistently on every open of the same database. Toggling it on an existing database corrupts reads.

### Compaction Filter

Provide an `ICompactionFilter` to logically delete entries based on key or value. Filtered entries are hidden at read time (no waiting for the next compaction) and physically removed when the relevant SSTables are merged.

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

The filter receives the decoded user value (TTL header already stripped when `EnableTtl` is `true`). The implementation must be thread-safe.

### Closing

`PithosDb` implements `IDisposable`. Use a `using` statement or call `Dispose()` explicitly to flush and close the WAL.

---

## Configuration

Pass a `PithosOptions` instance to tune the engine at open time. All properties have sensible defaults.

```csharp
using var db = new PithosDb("path/to/data-directory", new PithosOptions
{
    MemTableSizeThreshold        = 8 * 1024 * 1024, // 8 MB flush threshold
    BloomFilterFalsePositiveRate = 0.001,            // 0.1% false positive rate
    LevelCount                   = 5,                // 5 compaction levels
    LevelZeroFileCountLimit      = 4,                // compact L0 after 4 files
    LevelSizeMultiplier          = 10,               // each level is 10× the previous
    BlockCacheSizeBytes          = 32 * 1024 * 1024, // 32 MB block cache
    BlockCacheKind               = BlockCacheKind.S3Fifo, // S3-FIFO eviction
    Compression                  = CompressionKind.Lz4,   // LZ4 block compression
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
| `EnableTtl` | `false` | Enables per-entry TTL. Must be set consistently on every open of the same database |
| `CompactionFilter` | `null` | Optional `ICompactionFilter` applied at read time and compaction |

---

## Performance

Benchmarks run with [BenchmarkDotNet](https://benchmarkdotnet.org/) on .NET 9.0 · RyuJIT AVX2 · 12th Gen Intel Core i7-12700F (20 logical cores).

### Writes

| Method | EntryCount | Mean | Allocated |
|---|---|---|---|
| `SequentialPuts` | 1,000 | 5.91 ms | 612 KB |
| `RandomPuts` | 1,000 | 5.79 ms | 612 KB |
| `SequentialPuts` | 10,000 | 90.4 ms | 8,728 KB |
| `RandomPuts` | 10,000 | 97.4 ms | 8,728 KB |

Sequential and random key patterns perform nearly identically — `SortedDictionary` insertion cost is similar either way. Write throughput is dominated by fsync cost on the WAL.

### Reads

| Method | Mean | Allocated |
|---|---|---|
| `MemTableHit` | 20.9 ns | 0 B |
| `SSTableHit` | 1,479 ns | 2,192 B |
| `KeyMiss` | 1,761 ns | 2,160 B |

A MemTable hit costs **~21 ns** with zero allocation. An SSTable hit costs **~1.5 µs** — the bloom filter and sparse index are loaded once per file at open time and kept in memory; the only I/O per lookup is a single positional `RandomAccess.Read` call that fetches the relevant block. A miss costs ~19% more than a hit because the bloom filter (1% FPR) occasionally passes through a false positive that requires reading a block.

> **Optimization history:** the baseline opened and closed a full `SSTableReader` (bloom filter + index I/O) on every `TryGet` call — **~2 ms, 151 KB**. Three incremental improvements reduced this to the current numbers: (1) caching reader instances so bloom and index are always in memory, (2) replacing per-call `FileStream` opens with positional `RandomAccess` reads on a persistent handle, and (3) pre-reading the full block in one syscall before parsing, eliminating per-field I/O overhead on block scans. Net result: **>1,000× faster, 72× fewer allocations**.

### Block Cache

Benchmarks use a **256 KB cache** (intentionally small) against 5,000 entries to stress eviction behaviour. Reads follow a 90/10 pattern: 90% of accesses target the hot 10% of keys, with occasional cold-key sweeps to stress scan-pollution resistance.

| Method | Mean | Ratio | Allocated |
|---|---|---|---|
| `NoCache_HotRead` | 6,353 ns | 1.00 | 5,423 B |
| `Lru_HotRead` | 1,672 ns | 0.26 | 878 B |
| `S3Fifo_HotRead` | 1,663 ns | 0.26 | 878 B |
| `NoCache_Mixed` | 6,407 ns | 1.01 | 5,349 B |
| `Lru_Mixed` | 1,259 ns | 0.20 | 1,028 B |
| `S3Fifo_Mixed` | 1,149 ns | 0.18 | 1,022 B |

Both caches deliver **~3.8× speedup** on hot reads. On the mixed workload, S3-FIFO outperforms LRU by ~9% — cold-key scans land in S3-FIFO's small probation queue and are evicted without displacing hot blocks from the main queue.

### Block Compression

Read benchmarks with the block cache **disabled** so every lookup decompresses from disk, making the LZ4 overhead directly visible.

| Method | Mean | Ratio | Allocated |
|---|---|---|---|
| `Read_None` | 2,684 ns | 1.00 | 4.64 KB |
| `Read_Lz4` | 2,769 ns | 1.03 | 4.64 KB |

LZ4 decompression adds only **~3% read latency** on cache-cold reads. When the block cache is enabled the overhead is fully amortised — a block is decompressed once on first read and served from memory on all subsequent accesses. Enabling LZ4 reduces SSTable file sizes for compressible data with negligible read-path cost.

### Concurrency (500 ops per task)

| Method | Readers | Mean | vs. reads-only |
|---|---|---|---|
| `ConcurrentReadsOnly` | 2 | 300 µs | baseline |
| `ConcurrentReadWrite` | 2 | 2,028 µs | **6.8×** |
| `ConcurrentReadsOnly` | 8 | 780 µs | baseline |
| `ConcurrentReadWrite` | 8 | 2,641 µs | **3.4×** |

`ReaderWriterLockSlim` allows parallel reads; a single writer's exclusive lock blocks all readers. The penalty shrinks as reader count grows because each write-lock acquisition is amortised across more concurrent readers.

### Compaction

| Method | EntryCount | Mean | vs. no compaction |
|---|---|---|---|
| `WritesWithoutCompaction` | 500 | 5.64 ms | baseline |
| `WritesWithCompaction` | 500 | 6.41 ms | 1.14× |
| `WritesWithoutCompaction` | 1,000 | 13.67 ms | baseline |
| `WritesWithCompaction` | 1,000 | 15.96 ms | 1.17× |

At small data sizes the compaction overhead is modest — merging a few kilobytes of SSTable data is fast relative to the fsync cost of the WAL writes that trigger it.

### Running Benchmarks

```bash
dotnet run -c Release --project benchmarks/PithosDB.Benchmarks
```

To target a specific class:

```bash
dotnet run -c Release --project benchmarks/PithosDB.Benchmarks -- --filter "*ReadBenchmarks*"
```

Benchmarks must be run in Release mode. BenchmarkDotNet will error if invoked under Debug.

---

## Building

```bash
dotnet build
```

## Running Tests

```bash
dotnet test
```

Requires .NET 9 SDK.

---

## Contributing

### CI

Every push and pull request runs the full build and test suite via GitHub Actions ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)). All tests must pass before merging.

### Releasing

Releases are published to [NuGet.org](https://www.nuget.org) automatically when a version tag is pushed:

```bash
git tag v1.4.0
git push origin v1.4.0
```

The publish workflow ([`.github/workflows/publish.yml`](.github/workflows/publish.yml)) runs the test suite, packs both `PithosDB` and `PithosDB.Shell` at the tagged version, pushes to NuGet.org, and creates a GitHub Release with auto-generated notes. A `NUGET_API_KEY` secret must be configured under **repo → Settings → Secrets and variables → Actions**.
