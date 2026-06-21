# Architecture

PithosDB is an embedded key-value store built on an [LSM-tree](https://en.wikipedia.org/wiki/Log-structured_merge-tree) (Log-Structured Merge-Tree). The core idea of an LSM-tree is to convert random writes into sequential I/O: every write lands in an in-memory buffer and an append-only log, then is periodically flushed to disk as an immutable sorted file. This makes writes very fast (append-only) at the cost of reads requiring a search across multiple layers.

---

## Data Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                           Write path                                │
│                                                                     │
│  Put / Delete / WriteBatch                                          │
│        │                                                            │
│        ├──► WAL (append-only, fsynced)  ──► wal.log               │
│        │                                                            │
│        └──► MemTable (in-memory SortedDictionary)                  │
│                  │                                                  │
│             [size > threshold]                                      │
│                  │                                                  │
│                  └──► SSTableWriter ──► L0_<guid>.sst              │
│                             │                                       │
│                        [L0 full]                                    │
│                             │                                       │
│                             └──► LeveledCompactor ──► L1, L2, …   │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                           Read path                                 │
│                                                                     │
│  TryGet(key)                                                        │
│        │                                                            │
│        ├──1. MemTable  (most recent writes, O(log n))              │
│        │                                                            │
│        ├──2. L0 SSTables (newest → oldest within level)            │
│        │       └─ BloomFilter → sparse index → block I/O           │
│        │                                                            │
│        └──3. L1 … Ln SSTables (same per-file lookup)               │
│                                                                     │
│   Returns on first hit (including tombstones, which stop the       │
│   search and return "not found" to the caller).                    │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                           Scan path                                 │
│                                                                     │
│  Scan(from, to)                                                     │
│        │                                                            │
│        └──► k-way merge via PriorityQueue                          │
│               ├─ MemTable entries                                   │
│               └─ All SSTable levels (newest-first within level)     │
│                                                                     │
│   Duplicate keys: first occurrence wins (newest source).           │
│   Tombstones and expired TTL entries are filtered out.             │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Thread Safety

All public operations are thread-safe via a single `ReaderWriterLockSlim`:

- **Reads** (`TryGet`, `Scan`) acquire a shared read lock — multiple readers proceed concurrently.
- **Writes** (`Put`, `Delete`, `Write`) acquire an exclusive write lock — one writer blocks all readers for the duration of the MemTable mutation and the optional flush.
- **Async API** — `ReaderWriterLockSlim` has thread affinity and cannot span an `await`. Each async method offloads its synchronous counterpart to the thread pool via `Task.Run`, freeing the caller's thread during WAL fsyncs and SSTable reads.

---

## Components

### MemTable — `Core/MemTable.cs`

**What it is:** The active write buffer. All incoming writes land here first.

**How it works:**  
Internally a `SortedDictionary<byte[], byte[]?>` keyed by `ByteArrayComparer` (lexicographic byte order). Deleted keys are stored as `null` (tombstone) rather than being removed, so that a delete in the MemTable shadows older values that may exist in SSTables below.

`SizeBytes` tracks the approximate in-memory footprint. When it exceeds `PithosOptions.MemTableSizeThreshold` (default 4 MB), `MaybeFlushMemTable` in `PithosDb` triggers a flush to a new L0 SSTable.

**Use case:**  
Absorbs writes at in-memory speed. Because it is sorted, `GetSortedEntries()` produces the flush input in the exact order the `SSTableWriter` requires — no external sort step needed.

**In-memory mode:** When `PithosOptions.InMemory = true`, the MemTable is the entire store. The flush threshold is ignored and data accumulates in memory for the lifetime of the instance.

---

### Write-Ahead Log (WAL) — `Core/WriteAheadLog.cs`

**What it is:** The durability guarantee. Every write is appended here and fsynced before touching the MemTable.

**How it works:**  
An append-only `FileStream` opened in `FileMode.Append`. Each record is serialized into a `MemoryStream`, a CRC32 is computed over the entire record payload, and both are written together before `Flush()`. On startup, `Replay()` reads records sequentially, verifying each CRC. A truncated record or checksum mismatch causes replay to stop — earlier records are unaffected.

The log file is deleted and recreated after every successful MemTable flush to SSTable. This keeps the WAL small: it only ever needs to cover the unflushed MemTable.

**Batch atomicity:** `WriteBatch` operations are written as a single `Batch` record. The CRC covers the entire batch payload, so either all operations in a batch survive a crash or none do — there is no partial-batch recovery.

**Use case:**  
Crash recovery. Without the WAL, any unflushed MemTable contents would be lost on process crash. The WAL lets the engine replay up to `MemTableSizeThreshold` bytes of writes on the next open with no data loss.

**In-memory mode:** No WAL is created. `_wal` is `null` and all WAL call sites use `?.` (null-conditional), making them no-ops.

---

### SSTable — `Storage/SSTableWriter.cs`, `Storage/SSTableReader.cs`

**What it is:** The on-disk storage unit. Immutable once written.

**How it works:**  
`SSTableWriter` accepts a sorted entry sequence and writes it in fixed-size blocks (≤ 4 KB each). Each block is optionally compressed (LZ4), then written with a CRC32 checksum. After all data blocks, the file ends with a bloom filter section, a sparse index section, and a 16-byte footer pointing to both.

`SSTableReader` loads the bloom filter and sparse index into memory on open. For a point lookup:
1. Check the bloom filter — a definite miss skips all I/O for this file.
2. Binary-search the sparse index to find the block whose first key is ≤ the target key.
3. Read that one block via `RandomAccess.Read` (positional, does not advance the stream — safe for concurrent callers sharing the same handle).
4. Verify the CRC, decompress, then scan the block entries linearly.

**Block cache integration:** On a cache miss the block is decompressed, checksum-verified, and stored in the shared cache. Subsequent reads for the same block offset serve from memory with no I/O.

**Use case:**  
The persistence layer. Each SSTable is an immutable snapshot of one flush or compaction output. Immutability means files can be read concurrently, shared safely across threads, and deleted atomically once superseded by a compaction.

---

### Bloom Filter — `Core/BloomFilter.cs`

**What it is:** A probabilistic set membership structure that eliminates most unnecessary SSTable reads.

**How it works:**  
A bit-array with `k` independent hash functions derived from MurmurHash3 double-hashing. `MightContain` returns `false` with certainty on a miss (no false negatives), but may return `true` for keys not in the set (false positives at the configured rate, default 1%).

Each SSTable gets its own bloom filter, sized to the number of keys it contains. The filter is serialized into the SSTable file and deserialized into memory when the `SSTableReader` opens.

**Use case:**  
Skipping files during point lookups. For a key not present in an SSTable, the bloom filter rejects it without any block I/O in 99% of cases. At 1% FPR with 10 SSTable files, on average only 0.1 files require a disk read for a missing key. This is why a key miss (1,761 ns) costs only ~19% more than a hit (1,479 ns) rather than 10×.

---

### Block Cache — `Core/LruBlockCache.cs`, `Core/S3FifoBlockCache.cs`

**What it is:** A shared in-memory cache for decompressed SSTable blocks, keyed by `(filePath, blockOffset)`.

**How it works:**  
Both implementations satisfy `IBlockCache`. `Put(path, offset, bytes)` stores a decompressed block. `TryGet(path, offset)` returns it on a hit. `EvictFile(path)` removes all entries for a given file (called before the compactor deletes a source SSTable).

- **`LruBlockCache`** — `LinkedList<CacheEntry>` + `Dictionary<key, LinkedListNode>`. On a hit, the node is moved to the head. On eviction (when the byte budget is exceeded), the tail is removed. Simple and predictable.

- **`S3FifoBlockCache`** — Three-queue structure (small queue ~10%, main queue ~90%, ghost set). New blocks enter the small queue. If accessed again while in the small queue, they are promoted to the main queue. The ghost set tracks recently evicted keys: a re-admitted block skips the small queue and goes directly to main. On eviction from the main queue, a block with frequency > 1 gets one more chance (moved to the tail of main); one with frequency = 1 is evicted.

**Use case:**  
Eliminating repeated I/O for hot blocks. With an 8 MB cache (default), frequently-read SSTable blocks stay in memory. S3-FIFO is preferred for workloads with scan patterns or high key skew, because cold-key scans land in the small probation queue and are evicted without displacing hot blocks from main. LRU is simpler and performs comparably on uniform access patterns.

---

### Leveled Compactor — `Compaction/LeveledCompactor.cs`

**What it is:** The background process that merges SSTables across levels to bound read amplification and reclaim space from deleted/expired entries.

**How it works:**  
`CompactIfNeeded` is called after every MemTable flush. It checks each level against its file-count limit. When a level is full, `Compact` k-way merges all SSTables at that level into a single new SSTable at the level below, then deletes the source files.

The merge uses a `PriorityQueue` ordered by `(key asc, readerIndex desc)`. For duplicate keys across source files, the highest-indexed reader (newest file) wins. Tombstones are passed through so they can shadow older values in lower levels. TTL-expired entries and compaction-filtered entries are dropped during the merge.

**Crash safety:** The manifest is updated (new file added, source files removed) *before* source files are deleted. If the process crashes between those two steps, the source files become orphans that are cleaned up on the next open. The merged file is already in the manifest and usable.

**Level sizing:**  
With defaults (`LevelZeroFileCountLimit = 10`, `LevelSizeMultiplier = 10`, `LevelCount = 7`):

| Level | File limit |
|-------|-----------|
| L0 | 10 |
| L1 | 100 |
| L2 | 1,000 |
| L3 | 10,000 |
| … | … |

**Use case:**  
Bounding read amplification. Without compaction, reads would need to search an ever-growing pile of L0 files. Compaction merges them progressively into larger levels, so reads only ever scan a bounded number of files. Compaction also physically removes tombstones and expired TTL entries, reclaiming disk space.

---

### Manifest — `Storage/Manifest.cs`

**What it is:** A durable record of which SSTable files exist and which level they belong to.

**How it works:**  
Written as a simple serialized list of per-level file path lists. Read on startup to reconstruct the `_levels` structure without scanning the directory. If the manifest is missing, `PithosDb` falls back to scanning for `*.sst` files and inferring their level from the filename prefix (`L0_`, `L1_`, etc.).

**Use case:**  
Crash-safe level tracking. The compactor writes a new manifest after creating the merged file but before deleting source files. This ordering means the manifest always represents a consistent view of the level structure, even if the process crashes mid-compaction.

---

### ValueCodec — `Core/ValueCodec.cs`

**What it is:** The encoding layer for per-entry TTL metadata. Only active when `PithosOptions.EnableTtl = true`.

**How it works:**  
Every value stored on disk is prefixed with a 1-byte flag:

| Flag | Layout | Meaning |
|------|--------|---------|
| `0x00` | `[0x00][value…]` | Plain value, no expiry |
| `0x01` | `[0x01][expiry: 8 bytes LE][value…]` | Expires at the given Unix millisecond timestamp |

At read time, `Decode` strips the flag, checks the expiry timestamp against `DateTimeOffset.UtcNow`, and returns `null` for expired entries. At compaction time, `DecodeForCompaction` does the same but distinguishes "expired, drop it" from "live, re-emit the original encoded bytes" — the TTL header must be preserved so future reads and compactions can still evaluate it.

**Use case:**  
Session stores, rate-limit counters, ephemeral cache entries — any use case where entries should automatically disappear after a fixed duration. The flag byte adds 1 byte overhead per value (plain) or 9 bytes (TTL). Because the codec operates uniformly on all values when `EnableTtl = true`, the flag byte is present even for non-TTL puts, which is why the option must be set consistently on every open.

---

## Startup and Recovery

On every `new PithosDb(directory)`:

1. **WAL replay** — `WriteAheadLog.Replay` reads `wal.log` and re-applies all records to the MemTable. Stops at the first truncated or corrupt record.
2. **SSTable recovery** — `Manifest.TryRead` reconstructs `_levels` from the manifest. If the manifest is absent (first open, or manual deletion), the directory is scanned for `*.sst` files and levels are inferred from filenames.
3. **Orphan cleanup** — Any `.sst` files not listed in the manifest are deleted (left over from a crash mid-compaction).
4. **Reader cache warm-up** — An `SSTableReader` is opened for every known SSTable, loading its bloom filter and sparse index into memory.

This sequence ensures the database is always in a consistent, fully-indexed state before the first operation is served.
