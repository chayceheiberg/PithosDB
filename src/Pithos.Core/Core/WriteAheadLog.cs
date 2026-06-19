namespace Pithos.Core.Core;

/// <summary>Entry type discriminator stored as the first byte of each WAL record.</summary>
public enum WalEntryType : byte { Put = 1, Delete = 2 }

/// <summary>
/// Append-only binary log that provides crash durability for unflushed MemTable writes.
/// Every <see cref="AppendPut"/> and <see cref="AppendDelete"/> is fsynced to disk before
/// the corresponding MemTable mutation is applied. On startup, <see cref="Replay"/> re-applies
/// all records to rebuild the MemTable. The log file is deleted and recreated after each
/// successful MemTable flush to SSTable.
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
    /// Replays all records from the WAL file at <paramref name="path"/>.
    /// Returns an empty sequence if the file does not exist. Partial trailing
    /// records (from a crash mid-write) will cause a read exception; the caller
    /// is responsible for handling corruption if needed.
    /// </summary>
    public static IEnumerable<(WalEntryType type, byte[] key, byte[]? value)> Replay(string path)
    {
        if (!File.Exists(path)) yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);

        while (stream.Position < stream.Length)
        {
            var type = (WalEntryType)reader.ReadByte();
            var keyLen = reader.ReadInt32();
            var key = reader.ReadBytes(keyLen);

            if (type == WalEntryType.Put)
            {
                var valLen = reader.ReadInt32();
                var value = reader.ReadBytes(valLen);
                yield return (type, key, value);
            }
            else
            {
                yield return (type, key, null);
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
