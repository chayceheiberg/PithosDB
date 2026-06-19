using System.Text;
using Pithos.Core;

namespace Pithos.Tests;

public class ConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ConcurrencyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task ConcurrentWrites_AllKeysReadableAfterwards()
    {
        using var db = new PithosDb(_dir);
        const int threadCount = 20;
        const int writesPerThread = 50;

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < writesPerThread; i++)
                db.Put(K($"t{t}-k{i}"), V($"t{t}-v{i}"));
        }));

        await Task.WhenAll(tasks);

        for (int t = 0; t < threadCount; t++)
            for (int i = 0; i < writesPerThread; i++)
                Assert.True(db.TryGet(K($"t{t}-k{i}"), out _), $"Missing t{t}-k{i}");
    }

    [Fact]
    public async Task ConcurrentReads_WhileWriting_DoNotThrow()
    {
        using var db = new PithosDb(_dir);

        for (int i = 0; i < 100; i++)
            db.Put(K($"key-{i}"), V($"val-{i}"));

        using var cts = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 5).Select(n => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
                for (int i = 0; i < 100; i++)
                    db.TryGet(K($"key-{i}"), out var _);
        }, cts.Token));

        var writer = Task.Run(() =>
        {
            for (int i = 100; i < 300; i++)
                db.Put(K($"key-{i}"), V($"val-{i}"));
        });

        await writer;
        cts.Cancel();
        await Task.WhenAll(readers.Select(r => r.ContinueWith(_ => { })));
    }

    [Fact]
    public async Task ConcurrentReads_ReturnConsistentValues()
    {
        using var db = new PithosDb(_dir);
        const int keyCount = 100;

        for (int i = 0; i < keyCount; i++)
            db.Put(K($"key-{i}"), V($"val-{i}"));

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 10).Select(n => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < keyCount; i++)
                {
                    Assert.True(db.TryGet(K($"key-{i}"), out var value));
                    Assert.Equal(V($"val-{i}"), value);
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        }));

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentWritesAndDeletes_DoNotDeadlock()
    {
        using var db = new PithosDb(_dir);

        var writers = Enumerable.Range(0, 10).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
                db.Put(K($"t{t}-k{i}"), V($"v{i}"));
        }));

        var deleters = Enumerable.Range(0, 5).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
                db.Delete(K($"t{t}-k{i}"));
        }));

        await Task.WhenAll(writers.Concat(deleters));
    }
}
