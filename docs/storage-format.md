# Storage Format

This document describes the exact binary layout of every file PithosDB writes to disk. All multi-byte integers are **little-endian** unless noted.

---

## SSTable File

SSTables are immutable files written by `SSTableWriter` and read by `SSTableReader`. Every flush and every compaction output produces one new SSTable.

```
┌──────────────────────────────────────────────────────────────────┐
│  Data blocks                                                     │
│  (one or more, each ≤ 4 KB of uncompressed entry data)          │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ compression  : uint8   — 0 = none, 1 = LZ4                │  │
│  │ payloadLen   : int32   — byte length of the payload field  │  │
│  │ payload      : byte[]  — compressed or raw block data      │  │
│  │ CRC32        : uint32  — over compression + payloadLen + payload │
│  └────────────────────────────────────────────────────────────┘  │
│  (repeated for every block)                                      │
├──────────────────────────────────────────────────────────────────┤
│  Bloom filter section          (at offset bloomOffset)           │
│    hashCount   : int32         — number of hash functions        │
│    bitCount    : int32         — length of the bits array        │
│    bits        : bool[]        — one bool per bit (not packed)   │
├──────────────────────────────────────────────────────────────────┤
│  Sparse index section          (at offset indexOffset)           │
│    count       : int32         — number of index entries         │
│    entries     : (repeated)                                      │
│      keyLen    : int32                                           │
│      key       : byte[]        — first key of the block          │
│      offset    : int64         — byte offset of the block start  │
├──────────────────────────────────────────────────────────────────┤
│  Footer                        (last 16 bytes)                   │
│    bloomOffset : int64         — byte offset of bloom section    │
│    indexOffset : int64         — byte offset of index section    │
└──────────────────────────────────────────────────────────────────┘
```

### Data block payload layout

After decompression, each block payload is:

```
entryCount   : int32
entries      : (repeated entryCount times)
  keyLen     : int32
  key        : byte[]
  isTombstone: bool (1 byte — 0x00 = value follows, non-zero = tombstone)
  [if not tombstone]
    valLen   : int32
    value    : byte[]
```

Entries within a block are sorted in byte-lexicographic key order. Blocks within a file are also sorted by their first key.

### CRC32

The CRC32 covers `[compression byte][payloadLen bytes][payload bytes]` — everything in the block record except the 4-byte CRC itself. Computed with `System.IO.Hashing.Crc32`. A mismatch on read throws `InvalidDataException`.

### Block cache key

The block cache is keyed by `(filePath, blockOffset)` where `blockOffset` is the byte position of the `compression` byte in the file. Cached blocks are stored decompressed and checksum-verified; subsequent reads skip both steps.

---

## Write-Ahead Log (WAL)

The WAL lives at `{directory}/wal.log`. It is opened in append mode and truncated (deleted and recreated) after every successful MemTable flush. There are three record types.

### Put record

Written by `AppendPut`.

```
type         : uint8  = 0x01 (WalEntryType.Put)
keyLen       : int32
key          : byte[]
valLen       : int32
value        : byte[]
CRC32        : uint32  — over all preceding bytes of this record
```

### Delete record

Written by `AppendDelete`.

```
type         : uint8  = 0x02 (WalEntryType.Delete)
keyLen       : int32
key          : byte[]
CRC32        : uint32  — over all preceding bytes of this record
```

### Batch record

Written by `AppendBatch`. The CRC covers only the payload, not the type byte or length field.

```
type         : uint8  = 0x03 (WalEntryType.Batch)
payloadLen   : int32
payload      : byte[]  — concatenated Put/Delete sub-records (see below)
CRC32        : uint32  — over payload bytes only
```

Each sub-record inside the batch payload:

```
type         : uint8   (0x01 = Put, 0x02 = Delete)
keyLen       : int32
key          : byte[]
[if Put]
  valLen     : int32
  value      : byte[]
```

### Replay semantics

`WriteAheadLog.Replay` reads records sequentially. Any of the following causes replay to stop at that point (earlier records are unaffected and already re-applied):

- Fewer bytes remain than the next field requires (truncated record)
- CRC32 mismatch (corrupt record or partial write)

This guarantees that a crash mid-write never produces a partially-applied record.

---

## ValueCodec encoding

Used when `PithosOptions.EnableTtl = true`. Every value stored in the MemTable, WAL, and SSTables is wrapped with a 1-byte flag.

### Plain value (no TTL)

```
0x00         : uint8   (FlagPlain)
value        : byte[]  — user-supplied bytes
```

### TTL value

```
0x01         : uint8   (FlagTtl)
expiryMs     : int64   — Unix millisecond timestamp (UTC) at which the entry expires
value        : byte[]  — user-supplied bytes
```

`expiryMs` is computed at write time as `DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds()`.

At read time, if `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiryMs`, the entry is treated as if it does not exist (returns `null` / `false`).

During compaction, expired entries are dropped entirely (not written to the merged SSTable). Live TTL entries are re-emitted with their original encoded bytes (including the `0x01` flag and `expiryMs`) so the expiry can still be evaluated on future reads.

### Empty stored value

A zero-length byte array stored without TTL is represented as `[0x00]` — a single `FlagPlain` byte with no value bytes following. `Decode([0x00])` returns `[]`.

### Malformed TTL record

A record starting with `0x01` but shorter than 9 bytes total is treated as expired/malformed — `Decode` returns `null` and `DecodeForCompaction` returns `(null, dropped: true)`.

---

## Manifest file

The manifest lives at `{directory}/MANIFEST`. It records the current level structure so the engine can reconstruct `_levels` on startup without scanning the directory.

The format is implementation-defined (see `Storage/Manifest.cs`) and is not intended to be human-readable or externally consumed. If the manifest is absent, PithosDB falls back to scanning `*.sst` files and inferring their level from the filename prefix (`L0_`, `L1_`, etc.).
