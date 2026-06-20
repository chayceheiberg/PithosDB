using Pithos.Core;
using Pithos.Core.Storage;

namespace Pithos.Tests;

public class ManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ManifestTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── Manifest read/write round-trips ───────────────────────────────────

    [Fact]
    public void TryRead_ReturnsFalse_WhenNoFile()
    {
        var m = new Manifest(_dir);
        Assert.False(m.TryRead(out _));
    }

    [Fact]
    public void WriteAndRead_EmptyLevels_RoundTrips()
    {
        var m = new Manifest(_dir);
        m.Write([]);
        Assert.True(m.TryRead(out var levels));
        Assert.Empty(levels);
    }

    [Fact]
    public void WriteAndRead_MultipleLevels_RoundTrips()
    {
        var m = new Manifest(_dir);
        var input = new List<List<string>>
        {
            new() { "/db/L0_aaa.sst", "/db/L0_bbb.sst" },
            new() { "/db/L1_ccc.sst" },
            new(),
        };

        m.Write(input);
        Assert.True(m.TryRead(out var output));

        Assert.Equal(input.Count, output.Count);
        for (int i = 0; i < input.Count; i++)
            Assert.Equal(input[i], output[i]);
    }

    [Fact]
    public void TryRead_ReturnsFalse_OnCorruptFile()
    {
        var path = Path.Combine(_dir, "MANIFEST");
        File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]); // garbage

        var m = new Manifest(_dir);
        Assert.False(m.TryRead(out _));
    }

    [Fact]
    public void TryRead_ReturnsFalse_OnBitFlipInData()
    {
        var m = new Manifest(_dir);
        m.Write([["/db/L0_aaa.sst"]]);

        var path = Path.Combine(_dir, "MANIFEST");
        var bytes = File.ReadAllBytes(path);
        bytes[8] ^= 0xFF; // flip bits in the level-count field
        File.WriteAllBytes(path, bytes);

        Assert.False(m.TryRead(out _));
    }

    [Fact]
    public void Write_IsAtomic_TempFileRemovedAfterWrite()
    {
        var m = new Manifest(_dir);
        m.Write([["/db/L0_aaa.sst"]]);

        Assert.False(File.Exists(Path.Combine(_dir, "MANIFEST.tmp")));
        Assert.True(File.Exists(Path.Combine(_dir, "MANIFEST")));
    }

    [Fact]
    public void Write_Overwrites_PreviousManifest()
    {
        var m = new Manifest(_dir);
        m.Write([["/db/L0_aaa.sst"]]);
        m.Write([["/db/L0_bbb.sst"], ["/db/L1_ccc.sst"]]);

        Assert.True(m.TryRead(out var levels));
        Assert.Equal(2, levels.Count);
        Assert.Equal(["/db/L0_bbb.sst"], levels[0]);
        Assert.Equal(["/db/L1_ccc.sst"], levels[1]);
    }

    // ── Integration: PithosDb uses manifest on reopen ─────────────────────

    [Fact]
    public void PithosDb_WritesManifest_AfterFlush()
    {
        var opts = new PithosOptions { MemTableSizeThreshold = 1024 };
        var dbDir = Path.Combine(_dir, "db");

        using (var db = new PithosDb(dbDir, opts))
        {
            // Write enough to trigger a flush.
            for (int i = 0; i < 50; i++)
                db.Put(BitConverter.GetBytes(i), new byte[32]);
        }

        Assert.True(File.Exists(Path.Combine(dbDir, "MANIFEST")));
    }

    [Fact]
    public void PithosDb_RecoverFromManifest_ReadsCorrectValues()
    {
        var opts = new PithosOptions { MemTableSizeThreshold = 1024 };
        var dbDir = Path.Combine(_dir, "db_recover");

        using (var db = new PithosDb(dbDir, opts))
        {
            for (int i = 0; i < 100; i++)
                db.Put(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 10));
        }

        // Second open should use the manifest, not filename scanning.
        using (var db = new PithosDb(dbDir, opts))
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.True(db.TryGet(BitConverter.GetBytes(i), out var val));
                Assert.Equal(BitConverter.GetBytes(i * 10), val);
            }
        }
    }

    [Fact]
    public void PithosDb_OrphanedSstFile_DeletedOnOpen()
    {
        var opts = new PithosOptions { MemTableSizeThreshold = 1024 };
        var dbDir = Path.Combine(_dir, "db_orphan");

        using (var db = new PithosDb(dbDir, opts))
        {
            for (int i = 0; i < 50; i++)
                db.Put(BitConverter.GetBytes(i), new byte[32]);
        }

        // Plant a fake orphaned SSTable file (not in the manifest).
        var orphan = Path.Combine(dbDir, "L0_orphan_fake.sst");
        File.WriteAllBytes(orphan, [0x01, 0x02]);

        using (var db = new PithosDb(dbDir, opts)) { }

        Assert.False(File.Exists(orphan), "orphaned SST file should be removed on open");
    }
}
