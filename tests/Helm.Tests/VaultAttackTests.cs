using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>
/// Attacks on the loss-prevention guard, written as an attacker (a buggy or hostile server, or a stolen device token)
/// would try them. Each one must end with the deletions held, not applied.
/// </summary>
public sealed class VaultAttackTests : IDisposable
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

    [Fact]
    public async Task Deletions_hidden_between_thousands_of_filler_records_are_still_held()
    {
        var (a, b, ids) = await TwoDevicesWith(30);
        // 30 live records: threshold 6. Three deletions, then more than 20 pages of filler, then three more.
        // Each step is synced on its own so the server orders them exactly like this.
        foreach (var id in ids.Take(3)) a.Items.Delete(id);
        await Sync(a);
        for (var i = 0; i < 10_600; i++) a.Filler.Upsert($"f{i:00000}", new Item("filler", false));
        await Sync(a);
        foreach (var id in ids.Skip(3).Take(3)) a.Items.Delete(id);
        await Sync(a);

        Assert.Equal(SyncRunOutcome.Held, (await b.Engine.SyncNowAsync()).Outcome);
        Assert.True(b.Items.All().Count >= 27, "at most the first batch may have been applied");
    }

    [Fact]
    public async Task Deletions_trickled_a_few_per_sync_are_held_once_they_add_up()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        // Threshold 4; two deletions per run stay under it every time.
        var outcomes = new List<SyncRunOutcome>();
        for (var round = 0; round < 5; round++)
        {
            foreach (var id in ids.Skip(round * 2).Take(2)) a.Items.Delete(id);
            await Sync(a);
            _time.Advance(TimeSpan.FromHours(1));
            outcomes.Add((await b.Engine.SyncNowAsync()).Outcome);
        }

        Assert.Contains(SyncRunOutcome.Held, outcomes);
        Assert.True(b.Items.All().Count >= 16, $"{20 - b.Items.All().Count} deletions slipped through");
    }

    [Fact]
    public async Task The_deletion_count_starts_again_after_a_day_and_after_a_decision()
    {
        var (a, b, ids) = await TwoDevicesWith(20);
        foreach (var id in ids.Take(2)) a.Items.Delete(id);
        await Sync(a, b);
        _time.Advance(TimeSpan.FromHours(25));
        foreach (var id in ids.Skip(2).Take(2)) a.Items.Delete(id);
        await Sync(a, b);
        Assert.Equal(16, b.Items.All().Count);
    }

    private sealed record Item(string Title, bool Trashed);

    private sealed record Device(SyncEngine Engine, SyncedCollection<Item> Items, SyncedCollection<Item> Filler);

    private async Task<(Device A, Device B, List<string> Ids)> TwoDevicesWith(int count)
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        var ids = Enumerable.Range(0, count).Select(i => a.Items.Add(new Item($"item {i}", false))).ToList();
        await Sync(a, b);
        return (a, b, ids);
    }

    private Device NewDevice(string name)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var engine = Own(new SyncEngine(db, _server.Connect(), new InMemoryMasterKeyStore(_key), [], time: _time, debounce: TimeSpan.FromHours(1)));
        var items = Own(new SyncedCollection<Item>(engine, new SyncedCollectionOptions<Item>
        {
            Name = "items",
            ConflictPolicy = SyncConflictPolicy.KeepBoth,
            GuardDeletions = true,
            IsExpendable = item => item.Trashed,
        }));
        var filler = Own(new SyncedCollection<Item>(engine, new SyncedCollectionOptions<Item> { Name = "filler" }));
        return new Device(engine, items, filler);
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
