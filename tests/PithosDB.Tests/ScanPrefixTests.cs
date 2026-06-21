using PithosDB.Core;

namespace PithosDB.Tests;

public class ScanPrefixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ScanPrefixTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] B(params byte[] bytes) => bytes;
    private static byte[] V(int i) => BitConverter.GetBytes(i);

    // ── Basic prefix matching ─────────────────────────────────────────────

    [Fact]
    public void ScanPrefix_ReturnsOnlyKeysWithPrefix()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0x01, 0x01), V(1));
        db.Put(B(0x01, 0x02), V(2));
        db.Put(B(0x02, 0x01), V(3)); // different prefix
        db.Put(B(0x01, 0x03), V(4));

        var results = db.ScanPrefix(B(0x01)).ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.key[0] == 0x01));
    }

    [Fact]
    public void ScanPrefix_ResultsAreSorted()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0xAA, 0x03), V(3));
        db.Put(B(0xAA, 0x01), V(1));
        db.Put(B(0xAA, 0x02), V(2));

        var keys = db.ScanPrefix(B(0xAA)).Select(r => r.key[1]).ToList();

        Assert.Equal([0x01, 0x02, 0x03], keys);
    }

    [Fact]
    public void ScanPrefix_NoMatches_ReturnsEmpty()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0x01, 0x00), V(1));

        var results = db.ScanPrefix(B(0x02));

        Assert.Empty(results);
    }

    [Fact]
    public void ScanPrefix_ExactKeyMatch_ReturnsThatKey()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0xAA, 0xBB), V(42));
        db.Put(B(0xAA, 0xBC), V(99));

        var results = db.ScanPrefix(B(0xAA, 0xBB)).ToList();

        Assert.Single(results);
        Assert.Equal(B(0xAA, 0xBB), results[0].key);
        Assert.Equal(V(42), results[0].value);
    }

    [Fact]
    public void ScanPrefix_EmptyPrefix_ReturnsAll()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0x01), V(1));
        db.Put(B(0x02), V(2));
        db.Put(B(0xFF), V(3));

        var results = db.ScanPrefix([]);

        Assert.Equal(3, results.Count());
    }

    // ── Boundary / overflow cases ─────────────────────────────────────────

    [Fact]
    public void ScanPrefix_PrefixEndingIn0xFF_HandlesCarry()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0x01, 0xFF, 0x00), V(1));
        db.Put(B(0x01, 0xFF, 0xFF), V(2));
        db.Put(B(0x02, 0x00, 0x00), V(3)); // should NOT appear

        var results = db.ScanPrefix(B(0x01, 0xFF)).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(0x01, r.key[0]));
        Assert.All(results, r => Assert.Equal(0xFF, r.key[1]));
    }

    [Fact]
    public void ScanPrefix_AllFF_Prefix_ReturnsOnlyMatchingKeys()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0xFF, 0xFF, 0x01), V(1));
        db.Put(B(0xFF, 0xFF, 0x02), V(2));
        db.Put(B(0xFE, 0xFF), V(3)); // should NOT appear

        var results = db.ScanPrefix(B(0xFF, 0xFF)).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(0xFF, r.key[0]));
        Assert.All(results, r => Assert.Equal(0xFF, r.key[1]));
    }

    [Fact]
    public void ScanPrefix_SingleByteFF_ReturnsOnlyFFKeys()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0xFF, 0x01), V(1));
        db.Put(B(0xFF, 0x02), V(2));
        db.Put(B(0xFE, 0x99), V(3)); // should NOT appear

        var results = db.ScanPrefix(B(0xFF)).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(0xFF, r.key[0]));
    }

    // ── Interaction with delete ───────────────────────────────────────────

    [Fact]
    public void ScanPrefix_DeletedKey_NotReturned()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0xAA, 0x01), V(1));
        db.Put(B(0xAA, 0x02), V(2));
        db.Delete(B(0xAA, 0x01));

        var results = db.ScanPrefix(B(0xAA)).ToList();

        Assert.Single(results);
        Assert.Equal(B(0xAA, 0x02), results[0].key);
    }

    // ── In-memory mode ────────────────────────────────────────────────────

    [Fact]
    public void ScanPrefix_InMemory_Works()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(B(0x10, 0x01), V(1));
        db.Put(B(0x10, 0x02), V(2));
        db.Put(B(0x20, 0x01), V(3));

        var results = db.ScanPrefix(B(0x10)).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(0x10, r.key[0]));
    }

    // ── Async ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanPrefixAsync_ReturnsMatchingKeys()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0x55, 0x01), V(1));
        db.Put(B(0x55, 0x02), V(2));
        db.Put(B(0x66, 0x01), V(3));

        var results = new List<(byte[] key, byte[] value)>();
        await foreach (var entry in db.ScanPrefixAsync(B(0x55)))
            results.Add(entry);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(0x55, r.key[0]));
    }

    [Fact]
    public async Task ScanPrefixAsync_EmptyPrefix_ReturnsAll()
    {
        using var db = new PithosDb(_dir);
        db.Put(B(0x01), V(1));
        db.Put(B(0x02), V(2));

        var results = new List<(byte[] key, byte[] value)>();
        await foreach (var entry in db.ScanPrefixAsync([]))
            results.Add(entry);

        Assert.Equal(2, results.Count);
    }

    // ── String-prefix convenience ─────────────────────────────────────────

    [Fact]
    public void ScanPrefix_StringKeys_ReturnsCorrectSubset()
    {
        using var db = new PithosDb(_dir);
        static byte[] Str(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        static byte[] Val(string s) => System.Text.Encoding.UTF8.GetBytes(s);

        db.Put(Str("users/alice"), Val("alice-data"));
        db.Put(Str("users/bob"),   Val("bob-data"));
        db.Put(Str("posts/hello"), Val("post-data"));
        db.Put(Str("users/carol"), Val("carol-data"));

        var results = db.ScanPrefix(Str("users/")).ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
            Assert.StartsWith("users/", System.Text.Encoding.UTF8.GetString(r.key)));
    }
}
