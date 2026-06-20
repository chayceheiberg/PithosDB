using Pithos.Core.Core;

namespace Pithos.Core;

/// <summary>
/// An atomic batch of Put and Delete operations. When applied via
/// <see cref="PithosDb.Write"/>, all operations are written to the WAL in a
/// single CRC-guarded record: either every operation survives a crash or none do.
/// </summary>
public sealed class WriteBatch
{
    internal readonly List<(WalEntryType Type, byte[] Key, byte[]? Value)> Operations = [];

    /// <summary>Adds a Put operation to the batch.</summary>
    public WriteBatch Put(byte[] key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Operations.Add((WalEntryType.Put, key, value));
        return this;
    }

    /// <summary>Adds a Delete operation to the batch.</summary>
    public WriteBatch Delete(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Operations.Add((WalEntryType.Delete, key, null));
        return this;
    }
}
