using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Shell.ViewModels;

namespace Helm.Tests;

public sealed class SyncIndicatorTests
{
    [Fact]
    public async Task Not_set_up_opens_the_settings_and_set_up_syncs_now()
    {
        var sync = new FakeSync(new SyncStatus(SyncState.NotConfigured, null, null));
        var vm = new SyncIndicatorViewModel(sync, new Inline());
        var opened = 0;
        vm.OpenSettings = () => opened++;
        Assert.True(vm.IsNotSetUp);
        Assert.Contains("not set up", vm.ToolTip);

        await vm.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(1, opened);
        Assert.Equal(0, sync.Runs);

        sync.Set(new SyncStatus(SyncState.Idle, DateTimeOffset.Now, null));
        Assert.True(vm.IsSynced);
        Assert.Contains("Up to date", vm.ToolTip);
        await vm.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(1, sync.Runs);

        sync.Set(new SyncStatus(SyncState.Offline, null, "timeout"));
        Assert.True(vm.IsOffline);
        await vm.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(2, sync.Runs); // offline: trying again is what helps

        sync.Set(new SyncStatus(SyncState.Unauthorized, null, null));
        Assert.True(vm.HasProblem);
        await vm.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(2, sync.Runs); // a revoked token: the settings say what to do
        Assert.Equal(2, opened);

        sync.Set(new SyncStatus(SyncState.Syncing, null, null));
        Assert.True(vm.IsSyncing);
        Assert.False(vm.IsSynced);
    }

    [Fact]
    public async Task A_finished_sync_turns_green_for_a_moment()
    {
        var sync = new FakeSync(new SyncStatus(SyncState.Idle, DateTimeOffset.Now, null));
        var vm = new SyncIndicatorViewModel(sync, new Inline(), TimeSpan.FromMilliseconds(50));
        Assert.False(vm.JustSynced); // already synced at start: the usual colour

        sync.Set(new SyncStatus(SyncState.Offline, null, "timeout"));
        Assert.True(vm.IsMuted);
        Assert.False(vm.JustSynced); // offline → idle is not a finished sync

        sync.Set(new SyncStatus(SyncState.Syncing, null, null));
        Assert.False(vm.IsMuted);
        sync.Set(new SyncStatus(SyncState.Idle, DateTimeOffset.Now, null));
        Assert.True(vm.JustSynced);

        await Task.Delay(400);
        Assert.False(vm.JustSynced);

        sync.Set(new SyncStatus(SyncState.Syncing, null, null));
        sync.Set(new SyncStatus(SyncState.Idle, DateTimeOffset.Now, null));
        sync.Set(new SyncStatus(SyncState.Error, null, "boom"));
        Assert.False(vm.JustSynced); // a problem replaces the green at once
    }

    private sealed class Inline : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSync(SyncStatus status) : ISyncService
    {
        public int Runs { get; private set; }
        public SyncStatus Status { get; private set; } = status;
        public event EventHandler<SyncStatus>? StatusChanged;

        public void Set(SyncStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }

        public Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default)
        {
            Runs++;
            return Task.FromResult(new SyncRunResult(SyncRunOutcome.Completed, 0, 0, 0));
        }

        public void RequestSync() { }
    }
}
