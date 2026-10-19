using System.Diagnostics;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>When a requested sync runs: changes close together go in one run, a steady stream never holds it back for ever.</summary>
public sealed class SyncScheduleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _syncKey = SyncKeyring.CreateMasterKey();
    private readonly List<IDisposable> _owned = [];

    private SyncEngine Engine(TimeSpan debounce)
    {
        var paths = new HelmPaths(_dir);
        var db = new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local);
        _owned.Add(db);
        var engine = new SyncEngine(db, _server.Connect(), new InMemoryMasterKeyStore(_syncKey), [], debounce: debounce);
        _owned.Add(engine);
        return engine;
    }

    private static Task<TimeSpan> NextRunAsync(SyncEngine engine, Stopwatch clock)
    {
        var done = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StatusChanged += (_, s) =>
        {
            if (s.State != SyncState.Syncing) done.TrySetResult(clock.Elapsed);
        };
        return done.Task;
    }

    [Fact]
    public async Task A_steady_stream_of_requests_still_syncs_after_the_pause()
    {
        var engine = Engine(TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();
        var ran = NextRunAsync(engine, clock);
        // A session publishing every 100 ms used to push the run back each time: it never went out while Claude worked.
        while (!ran.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            engine.RequestSync();
            await Task.Delay(100);
        }
        Assert.True(ran.IsCompleted, "no sync ran while requests kept coming");
        Assert.True(await ran < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_command_goes_out_at_once()
    {
        var engine = Engine(TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew();
        var ran = NextRunAsync(engine, clock);
        engine.RequestSync();
        engine.RequestSyncSoon();
        Assert.True(await ran.WaitAsync(TimeSpan.FromSeconds(5)) < TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
