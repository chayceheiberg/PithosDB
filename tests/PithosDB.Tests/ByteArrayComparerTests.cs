using PithosDB.Core.Core;

namespace PithosDB.Tests;

public class ByteArrayComparerTests
{
    private static readonly ByteArrayComparer Cmp = ByteArrayComparer.Instance;

    // ── Null handling ─────────────────────────────────────────────────────────

    [Fact]
    public void Compare_BothNull_ReturnsZero()
        => Assert.Equal(0, Cmp.Compare(null, null));

    [Fact]
    public void Compare_LeftNull_ReturnsNegative()
        => Assert.True(Cmp.Compare(null, [1]) < 0);

    [Fact]
    public void Compare_RightNull_ReturnsPositive()
        => Assert.True(Cmp.Compare([1], null) > 0);

    // ── Equality ──────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_EqualArrays_ReturnsZero()
        => Assert.Equal(0, Cmp.Compare([1, 2, 3], [1, 2, 3]));

    [Fact]
    public void Compare_BothEmpty_ReturnsZero()
        => Assert.Equal(0, Cmp.Compare([], []));

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_LeftLessThanRight_ReturnsNegative()
        => Assert.True(Cmp.Compare([1], [2]) < 0);

    [Fact]
    public void Compare_LeftGreaterThanRight_ReturnsPositive()
        => Assert.True(Cmp.Compare([2], [1]) > 0);

    [Fact]
    public void Compare_DiffersByLaterByte_ReturnsCorrectSign()
    {
        Assert.True(Cmp.Compare([1, 2, 3], [1, 2, 4]) < 0);
        Assert.True(Cmp.Compare([1, 2, 4], [1, 2, 3]) > 0);
    }

    // ── Prefix / length ───────────────────────────────────────────────────────

    [Fact]
    public void Compare_ShorterPrefix_IsLessThanLonger()
        => Assert.True(Cmp.Compare([1, 2], [1, 2, 3]) < 0);

    [Fact]
    public void Compare_LongerArray_IsGreaterThanItsPrefix()
        => Assert.True(Cmp.Compare([1, 2, 3], [1, 2]) > 0);

    [Fact]
    public void Compare_EmptyArray_IsLessThanNonEmpty()
        => Assert.True(Cmp.Compare([], [0]) < 0);

    // ── Singleton ─────────────────────────────────────────────────────────────

    [Fact]
    public void Instance_IsSameReference()
        => Assert.Same(ByteArrayComparer.Instance, ByteArrayComparer.Instance);
}
