using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>The mass-deletion guard (SyncChangeGuard): bulk deletions of a guarded collection wait for the user.</summary>
public sealed class SyncChangeGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _key = SyncKeyring.CreateMasterKey();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(10, 3)]
    [InlineData(20, 4)]
    [InlineData(49, 10)]
    [InlineData(1000, 10)]
    public void Threshold_is_a_fifth_of_the_live_records_between_3_and_10(int live, int expected) =>
        Assert.Equal(expected, SyncChangeGuard.Threshold(live));

    [Fact]
    public async Task Deletions_below_the_threshold_are_applied()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        a.Items.Delete(ids[0]);
        a.Items.Delete(ids[1]);
        await Sync(a, b);
        Assert.Equal(18, b.Items.All().Count);
        Assert.Null(b.Engine.Hold);
    }

    [Fact]
    public async Task Bulk_deletions_are_held_and_nothing_of_the_pull_is_applied()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        foreach (var id in ids.Take(4)) a.Items.Delete(id);
        a.Items.Upsert(ids[10], new Item("edited", Trashed: false));
        await Sync(a);

        var result = await b.Engine.SyncNowAsync();

        Assert.Equal(SyncRunOutcome.Held, result.Outcome);
        Assert.Equal(SyncState.Held, b.Engine.Status.State);
        var hold = Assert.IsType<SyncHold>(b.Engine.Hold);
        Assert.Equal("items", hold.Collection);
        Assert.Equal(20, hold.LiveRecords);
        Assert.Equal(ids.Take(4).Order(), hold.Records.Select(r => r.Id).Order());
        Assert.Equal(20, b.Items.All().Count);
        Assert.Equal("item 10", b.Items.Get(ids[10])!.Title);

        // Held again on the next run: the cursor did not move.
        Assert.Equal(SyncRunOutcome.Held, (await b.Engine.SyncNowAsync()).Outcome);
    }

    [Fact]
    public async Task Local_edits_are_still_pushed_while_held()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        foreach (var id in ids.Take(5)) a.Items.Delete(id);
        await Sync(a);
        b.Items.Upsert(ids[15], new Item("from b", Trashed: false));

        Assert.Equal(SyncRunOutcome.Held, (await b.Engine.SyncNowAsync()).Outcome);
        await Sync(a);
        Assert.Equal("from b", a.Items.Get(ids[15])!.Title);
    }

    [Fact]
    public async Task Approving_applies_the_held_deletions()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        foreach (var id in ids.Take(6)) a.Items.Delete(id);
        await Sync(a);
        Assert.Equal(SyncRunOutcome.Held, (await b.Engine.SyncNowAsync()).Outcome);

        Assert.Equal(SyncRunOutcome.Completed, (await b.Engine.ApproveHeldAsync()).Outcome);

        Assert.Equal(14, b.Items.All().Count);
        Assert.Null(b.Engine.Hold);
        Assert.Equal(SyncState.Idle, b.Engine.Status.State);
    }

    [Fact]
    public async Task Rejecting_restores_the_records_on_every_device()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        foreach (var id in ids.Take(8)) a.Items.Delete(id);
        await Sync(a);
        Assert.Equal(12, a.Items.All().Count);
        Assert.Equal(SyncRunOutcome.Held, (await b.Engine.SyncNowAsync()).Outcome);

        Assert.Equal(SyncRunOutcome.Completed, (await b.Engine.RejectHeldAsync()).Outcome);
        await Sync(a);

        Assert.Equal(20, b.Items.All().Count);
        Assert.Equal(20, a.Items.All().Count);
        Assert.Equal("item 0", a.Items.Get(ids[0])!.Title);
        Assert.Null(b.Engine.Hold);
    }

    [Fact]
    public async Task Deleting_expendable_records_is_not_counted()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        foreach (var id in ids.Take(8)) a.Items.Upsert(id, new Item("trashed", Trashed: true));
        await Sync(a, b);
        foreach (var id in ids.Take(8)) a.Items.Delete(id);
        await Sync(a);

        Assert.Equal(SyncRunOutcome.Completed, (await b.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(12, b.Items.All().Count);
    }

    [Fact]
    public async Task Collections_without_a_guard_are_not_held()
    {
        var a = NewDevice("a", guard: false);
        var b = NewDevice("b", guard: false);
        var ids = Enumerable.Range(0, 10).Select(i => a.Items.Add(new Item($"item {i}", false))).ToList();
        await Sync(a, b);
        foreach (var id in ids) a.Items.Delete(id);
        await Sync(a, b);
        Assert.Empty(b.Items.All());
    }

    [Fact]
    public async Task Deletions_spread_over_several_pages_are_judged_together()
    {
        // 600 live records: threshold 10. Five deletions land in the first page of the next pull, five in the second,
        // with 600 edits in between, so no single page reaches the threshold.
        var (a, b, ids) = await TwoDevicesWith(600);
        foreach (var id in ids.Take(5)) a.Items.Delete(id);
        foreach (var id in ids.Skip(10)) a.Items.Upsert(id, new Item("edited", false));
        foreach (var id in ids.Skip(5).Take(5)) a.Items.Delete(id);
        await Sync(a);

        Assert.Equal(SyncRunOutcome.Held, (await b.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(10, b.Engine.Hold!.Records.Count);
        Assert.Equal(600, b.Items.All().Count);
        Assert.Equal("item 20", b.Items.Get(ids[20])!.Title);
    }

    private sealed record Item(string Title, bool Trashed);

    private sealed record Device(SyncEngine Engine, SyncedCollection<Item> Items);

    private async Task<(Device A, Device B, List<string> Ids)> TwoDevicesWith(int count)
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        var ids = Enumerable.Range(0, count).Select(i => a.Items.Add(new Item($"item {i}", false))).ToList();
        await Sync(a, b);
        Assert.Equal(count, b.Items.All().Count);
        return (a, b, ids);
    }

    private Device NewDevice(string name, bool guard = true)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var engine = Own(new SyncEngine(db, _server.Connect(), new InMemoryMasterKeyStore(_key), [], time: _time,
            debounce: TimeSpan.FromHours(1)));
        var items = Own(new SyncedCollection<Item>(engine, new SyncedCollectionOptions<Item>
        {
            Name = "items",
            ConflictPolicy = SyncConflictPolicy.KeepBoth,
            GuardDeletions = guard,
            IsExpendable = item => item.Trashed,
        }));
        return new Device(engine, items);
    }

    private static async Task Sync(params Device[] devices)
    {
        foreach (var device in devices) Assert.Equal(SyncRunOutcome.Completed, (await device.Engine.SyncNowAsync()).Outcome);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }
}
