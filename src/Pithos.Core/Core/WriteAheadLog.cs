using System.IO.Hashing;

namespace Pithos.Core.Core;

/// <summary>Entry type discriminator stored as the first byte of each WAL record.</summary>
public enum WalEntryType : byte { Put = 1, Delete = 2, Batch = 3 }

/// <summary>
/// Append-only binary log that provides crash durability for unflushed MemTable writes.
/// Every <see cref="AppendPut"/>, <see cref="AppendDelete"/>, and <see cref="AppendBatch"/>
/// is fsynced to disk before the corresponding MemTable mutation is applied. On startup,
/// <see cref="Replay"/> re-applies all records to rebuild the MemTable. The log file is
/// deleted and recreated after each successful MemTable flush to SSTable.
/// </summary>
public sealed class WriteAheadLog : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;

    /// <summary>
    /// Opens or creates the WAL file at <paramref name="path"/> in append mode.
    /// </summary>
    public WriteAheadLog(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream);
    }

    /// <summary>
    /// Appends a PUT record for <paramref name="key"/> / <paramref name="value"/>
    /// and flushes to disk.
    /// </summary>
    public void AppendPut(byte[] key, byte[] value)
    {
        _writer.Write((byte)WalEntryType.Put);
        _writer.Write(key.Length);
        _writer.Write(key);
        _writer.Write(value.Length);
        _writer.Write(value);
        _stream.Flush();
    }

    /// <summary>
    /// Appends a DELETE record for <paramref name="key"/> and flushes to disk.
    /// </summary>
    public void AppendDelete(byte[] key)
    {
        _writer.Write((byte)WalEntryType.Delete);
        _writer.Write(key.Length);
        _writer.Write(key);
        _stream.Flush();
    }

    /// <summary>
    /// Appends a BATCH record containing all <paramref name="operations"/> in a single
    /// CRC32-guarded write, then flushes to disk. The record format is:
    /// <c>[Batch(1B)][PayloadLen(4B)][Payload][CRC32(4B)]</c> where the CRC covers
    /// the payload bytes. On replay, a missing or mismatched CRC causes the record
    /// (and all subsequent records) to be silently dropped, preserving atomicity.
    /// </summary>
    internal void AppendBatch(List<(WalEntryType Type, byte[] Key, byte[]? Value)> operations)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (type, key, value) in operations)
            {
                bw.Write((byte)type);
                bw.Write(key.Length);
                bw.Write(key);
                if (type == WalEntryType.Put)
                {
                    bw.Write(value!.Length);
                    bw.Write(value);
                }
            }
        }

        var payload = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        uint crc = Crc32.HashToUInt32(payload);

        _writer.Write((byte)WalEntryType.Batch);
        _writer.Write((int)ms.Length);
        _writer.Write(payload);
        _writer.Write(crc);
        _stream.Flush();
    }

    /// <summary>
    /// Replays all records from the WAL file at <paramref name="path"/>.
    /// Returns an empty sequence if the file does not exist.
    /// <para>
    /// Partial trailing records (e.g. from a crash mid-write) are silently
    /// discarded: for individual Put/Delete records an incomplete read will
    /// surface as an EOF exception propagated to the caller; for Batch records
    /// a truncated payload or a CRC mismatch causes replay to stop early,
    /// guaranteeing that either all operations in a batch are applied or none are.
    /// </para>
    /// </summary>
    public static IEnumerable<(WalEntryType type, byte[] key, byte[]? value)> Replay(string path)
    {
        if (!File.Exists(path)) yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);

        while (stream.Position < stream.Length)
        {
            var type = (WalEntryType)reader.ReadByte();

            if (type == WalEntryType.Batch)
            {
                // Require full [PayloadLen(4)][Payload][CRC32(4)] — stop on truncation.
                if (stream.Position + 4 > stream.Length) yield break;
                int payloadLen = reader.ReadInt32();
                if (stream.Position + payloadLen + 4 > stream.Length) yield break;

                var payload = reader.ReadBytes(payloadLen);
                uint storedCrc = reader.ReadUInt32();

                if (Crc32.HashToUInt32(payload) != storedCrc) yield break;

                using var ms = new MemoryStream(payload);
                using var batchReader = new BinaryReader(ms);
                while (ms.Position < ms.Length)
                {
                    var opType = (WalEntryType)batchReader.ReadByte();
                    int keyLen = batchReader.ReadInt32();
                    var key = batchReader.ReadBytes(keyLen);
                    if (opType == WalEntryType.Put)
                    {
                        int valLen = batchReader.ReadInt32();
                        var value = batchReader.ReadBytes(valLen);
                        yield return (opType, key, value);
                    }
                    else
                    {
                        yield return (opType, key, null);
                    }
                }
            }
            else
            {
                int keyLen = reader.ReadInt32();
                var key = reader.ReadBytes(keyLen);

                if (type == WalEntryType.Put)
                {
                    int valLen = reader.ReadInt32();
                    var value = reader.ReadBytes(valLen);
                    yield return (type, key, value);
                }
                else
                {
                    yield return (type, key, null);
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}
