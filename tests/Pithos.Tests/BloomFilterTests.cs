using Pithos.Core.Core;

namespace Pithos.Tests;

public class BloomFilterTests
{
    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void MightContain_ReturnsTrueForAddedKey()
    {
        var bloom = new BloomFilter(100);
        bloom.Add(K("hello"));

        Assert.True(bloom.MightContain(K("hello")));
    }

    [Fact]
    public void MightContain_ReturnsFalseForClearlyAbsentKey()
    {
        var bloom = new BloomFilter(1000);
        bloom.Add(K("present"));

        // With 1000 capacity and 1% FPR there's a negligible chance of
        // a false positive on an entirely different key — treat as definite miss.
        Assert.False(bloom.MightContain(K("definitely-not-added-xyzzy-12345")));
    }

    [Fact]
    public void MightContain_ReturnsTrueForAllAddedKeys()
    {
        var bloom = new BloomFilter(100);
        var keys = Enumerable.Range(0, 50).Select(i => K($"key-{i}")).ToList();

        foreach (var key in keys)
            bloom.Add(key);

        foreach (var key in keys)
            Assert.True(bloom.MightContain(key));
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesLookups()
    {
        var bloom = new BloomFilter(100);
        bloom.Add(K("apple"));
        bloom.Add(K("banana"));

        var (bits, hashCount) = bloom.Serialize();
        var restored = new BloomFilter(bits, hashCount);

        Assert.True(restored.MightContain(K("apple")));
        Assert.True(restored.MightContain(K("banana")));
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesAbsenceLookups()
    {
        var bloom = new BloomFilter(1000);
        bloom.Add(K("only-key"));

        var (bits, hashCount) = bloom.Serialize();
        var restored = new BloomFilter(bits, hashCount);

        Assert.False(restored.MightContain(K("definitely-not-added-xyzzy-12345")));
    }
}
