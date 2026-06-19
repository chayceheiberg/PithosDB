namespace Pithos.Core.Core;

/// <summary>
/// Byte-lexicographic comparer for <see cref="T:byte[]"/> keys, used as the
/// sort order for <see cref="MemTable"/> and SSTable index lookups.
/// </summary>
public sealed class ByteArrayComparer : IComparer<byte[]>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly ByteArrayComparer Instance = new();

    /// <inheritdoc/>
    public int Compare(byte[]? x, byte[]? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
