using System.Buffers.Binary;
using PithosDB.Core.Core;

namespace PithosDB.Tests;

public class ValueCodecTests
{
    // ── Encode ────────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_PrefixesWithFlagPlain()
    {
        var result = ValueCodec.Encode([1, 2, 3]);
        Assert.Equal(ValueCodec.FlagPlain, result[0]);
        Assert.Equal([1, 2, 3], result[1..]);
    }

    [Fact]
    public void Encode_EmptyValue_ReturnsSingleFlagByte()
    {
        var result = ValueCodec.Encode([]);
        Assert.Equal([ValueCodec.FlagPlain], result);
    }

    // ── EncodeWithExpiry ──────────────────────────────────────────────────────

    [Fact]
    public void EncodeWithExpiry_PrefixesWithFlagTtlAndTimestamp()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var result = ValueCodec.EncodeWithExpiry([0xAB], expiry);

        Assert.Equal(ValueCodec.FlagTtl, result[0]);
        long storedMs = BinaryPrimitives.ReadInt64LittleEndian(result.AsSpan(1, 8));
        Assert.Equal(expiry.ToUnixTimeMilliseconds(), storedMs);
        Assert.Equal(new byte[] { 0xAB }, result[9..]);
    }

    [Fact]
    public void EncodeWithExpiry_EmptyValue_Returns9ByteHeader()
    {
        var result = ValueCodec.EncodeWithExpiry([], DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(9, result.Length);
        Assert.Equal(ValueCodec.FlagTtl, result[0]);
    }

    // ── Decode ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_FlagPlain_ReturnsUserValue()
    {
        var encoded = ValueCodec.Encode([10, 20, 30]);
        var decoded = ValueCodec.Decode(encoded);
        Assert.Equal([10, 20, 30], decoded);
    }

    [Fact]
    public void Decode_FlagPlain_EmptyValue_ReturnsEmptyArray()
    {
        var encoded = ValueCodec.Encode([]);
        var decoded = ValueCodec.Decode(encoded);
        Assert.Equal([], decoded);
    }

    [Fact]
    public void Decode_FlagTtl_NotExpired_ReturnsUserValue()
    {
        var encoded = ValueCodec.EncodeWithExpiry([7, 8, 9], DateTimeOffset.UtcNow.AddHours(1));
        var decoded = ValueCodec.Decode(encoded);
        Assert.Equal([7, 8, 9], decoded);
    }

    [Fact]
    public void Decode_FlagTtl_Expired_ReturnsNull()
    {
        var encoded = ValueCodec.EncodeWithExpiry([1], DateTimeOffset.UtcNow.AddMilliseconds(-1));
        Assert.Null(ValueCodec.Decode(encoded));
    }

    [Fact]
    public void Decode_EmptyStored_ReturnsEmpty()
    {
        // Zero-length stored value is a special edge case: returned as-is.
        Assert.Equal([], ValueCodec.Decode([]));
    }

    [Fact]
    public void Decode_FlagTtl_Malformed_ReturnsNull()
    {
        // FlagTtl byte but fewer than 9 bytes total — malformed.
        var malformed = new byte[] { ValueCodec.FlagTtl, 0x01, 0x02 };
        Assert.Null(ValueCodec.Decode(malformed));
    }

    [Fact]
    public void Decode_UnknownFlag_TreatedAsPlain()
    {
        // Any flag byte other than 0x00 or 0x01 falls through to the plain path.
        var stored = new byte[] { 0xFF, 0xAA, 0xBB };
        var decoded = ValueCodec.Decode(stored);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, decoded);
    }

    // ── DecodeForCompaction ───────────────────────────────────────────────────

    [Fact]
    public void DecodeForCompaction_Null_PassesTombstoneThrough()
    {
        var (value, dropped) = ValueCodec.DecodeForCompaction(null);
        Assert.Null(value);
        Assert.False(dropped);
    }

    [Fact]
    public void DecodeForCompaction_FlagPlain_ReturnsDecodedValue()
    {
        var encoded = ValueCodec.Encode([1, 2]);
        var (value, dropped) = ValueCodec.DecodeForCompaction(encoded);
        Assert.Equal([1, 2], value);
        Assert.False(dropped);
    }

    [Fact]
    public void DecodeForCompaction_EmptyStored_ReturnsEmpty()
    {
        var (value, dropped) = ValueCodec.DecodeForCompaction([]);
        Assert.Equal([], value);
        Assert.False(dropped);
    }

    [Fact]
    public void DecodeForCompaction_FlagTtl_NotExpired_ReturnsValue()
    {
        // Live TTL entry — kept during compaction (line 74).
        var encoded = ValueCodec.EncodeWithExpiry([5, 6], DateTimeOffset.UtcNow.AddHours(1));
        var (value, dropped) = ValueCodec.DecodeForCompaction(encoded);
        Assert.Equal([5, 6], value);
        Assert.False(dropped);
    }

    [Fact]
    public void DecodeForCompaction_FlagTtl_Expired_ReturnsDropped()
    {
        var encoded = ValueCodec.EncodeWithExpiry([1], DateTimeOffset.UtcNow.AddMilliseconds(-1));
        var (value, dropped) = ValueCodec.DecodeForCompaction(encoded);
        Assert.Null(value);
        Assert.True(dropped);
    }

    [Fact]
    public void DecodeForCompaction_FlagTtl_Malformed_ReturnsDropped()
    {
        var malformed = new byte[] { ValueCodec.FlagTtl, 0x01 };
        var (value, dropped) = ValueCodec.DecodeForCompaction(malformed);
        Assert.Null(value);
        Assert.True(dropped);
    }

    [Fact]
    public void DecodeForCompaction_UnknownFlag_TreatedAsPlain()
    {
        // Unknown flag byte falls through to the plain path (line 77).
        var stored = new byte[] { 0x42, 0xDE, 0xAD };
        var (value, dropped) = ValueCodec.DecodeForCompaction(stored);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, value);
        Assert.False(dropped);
    }

    // ── Round-trips ───────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_Encode_Decode()
    {
        var original = new byte[] { 0x01, 0x02, 0x03 };
        Assert.Equal(original, ValueCodec.Decode(ValueCodec.Encode(original)));
    }

    [Fact]
    public void RoundTrip_EncodeWithExpiry_Decode_BeforeExpiry()
    {
        var original = new byte[] { 0xAA, 0xBB };
        var encoded = ValueCodec.EncodeWithExpiry(original, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(original, ValueCodec.Decode(encoded));
    }
}
