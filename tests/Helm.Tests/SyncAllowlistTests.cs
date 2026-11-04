using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>
/// A device that syncs only an allowlist of collection prefixes (the headless connector): nothing else is downloaded
/// or uploaded, and widening the list later downloads the new part in full. Runs against <see cref="FakeSyncServer"/>.
/// </summary>
public sealed class SyncAllowlistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _key = SyncKeyring.CreateMasterKey();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_collection_outside_the_list_is_never_stored_here()
    {
        var pc = NewDevice("pc");
        pc.Wallet.Upsert("w1", new Item("coffee"));
        pc.Notes.Upsert("n1", new Item("diary"));
        await SyncAll(pc);
        _server.PullFilters.Clear();

        var box = NewDevice("box", ["wallet."]);
        await SyncAll(box);

        Assert.Equal(new Item("coffee"), box.Wallet.Get("w1"));
        Assert.Null(box.Notes.Get("n1"));
        Assert.Empty(box.Engine.Database.List("notes.items"));
        // The server is asked for the list only: the rest never leaves it.
        Assert.Contains(_server.PullFilters, f => f.Only is ["wallet."]);
        Assert.DoesNotContain(_server.PullFilters, f => f.Only is null);
    }

    [Fact]
    public async Task Local_writes_outside_the_list_are_never_pushed()
    {
        var pc = NewDevice("pc");
        var box = NewDevice("box", ["wallet."]);
        box.Notes.Upsert("local", new Item("written on the box"));
        box.Wallet.Upsert("w2", new Item("lunch"));
        await SyncAll(box, pc);

        Assert.Equal(new Item("lunch"), pc.Wallet.Get("w2"));
        Assert.Null(pc.Notes.Get("local"));
        Assert.DoesNotContain(_server.Records, r => r.Collection == "notes.items");
    }

    [Fact]
    public async Task Widening_the_list_later_pulls_that_group_in_full()
    {
        var pc = NewDevice("pc");
        pc.Notes.Upsert("old", new Item("before the box"));
        await SyncAll(pc);
        var box = NewDevice("box", ["wallet."]);
        await SyncAll(box);
        // Written while the box skips notes: its main cursor goes past these.
        pc.Notes.Upsert("new", new Item("while skipped"));
        pc.Wallet.Upsert("w", new Item("tea"));
        box.Notes.Upsert("mine", new Item("local note"));
        await SyncAll(pc, box);
        Assert.Null(box.Notes.Get("new"));

        // Restarted with notes on the list: the replica remembers the old list and catches up on the new prefix.
        var widened = NewDevice("box", ["notes.", "wallet."]);
        await SyncAll(widened, pc);

        Assert.Equal(new Item("before the box"), widened.Notes.Get("old"));
        Assert.Equal(new Item("while skipped"), widened.Notes.Get("new"));
        Assert.Equal(new Item("tea"), widened.Wallet.Get("w"));
        // Its own edit, kept while notes were off, goes up now.
        Assert.Equal(new Item("local note"), pc.Notes.Get("mine"));
        Assert.Contains(_server.PullFilters, f => f.Only is ["notes."]);
    }

    [Fact]
    public async Task Without_a_list_a_device_syncs_everything_and_dropping_a_list_downloads_the_rest()
    {
        var pc = NewDevice("pc");
        pc.Notes.Upsert("n", new Item("diary"));
        await SyncAll(pc);
        var box = NewDevice("box", ["wallet."]);
        await SyncAll(box);
        Assert.Null(box.Notes.Get("n"));
        Assert.Null(pc.Engine.IncludedPrefixes);

        box.Engine.SetIncluded(null);
        await SyncAll(box);

        Assert.Null(box.Engine.IncludedPrefixes);
        Assert.Equal(new Item("diary"), box.Notes.Get("n"));
    }

    [Fact]
    public void A_list_holds_prefixes_only()
    {
        var box = NewDevice("box");

        Assert.Throws<ArgumentException>(() => box.Engine.SetIncluded(["wallet"]));
        Assert.Throws<ArgumentException>(() => box.Engine.SetIncluded([]));
        box.Engine.SetIncluded(["wallet.", "ai."]);
        Assert.Equal(["ai.", "wallet."], box.Engine.IncludedPrefixes);
        Assert.True(box.Engine.IsExcluded("notes.items"));
        Assert.False(box.Engine.IsExcluded("wallet.transactions"));
    }

    private Device NewDevice(string name, IReadOnlyList<string>? only = null)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var engine = Own(new SyncEngine(db, _server.Connect(), new InMemoryMasterKeyStore(_key), [], debounce: TimeSpan.FromHours(1)));
        if (only is not null) engine.SetIncluded(only);
        var wallet = Own(new SyncedCollection<Item>(engine, new() { Name = "wallet.items" }));
        var notes = Own(new SyncedCollection<Item>(engine, new() { Name = "notes.items" }));
        return new Device(engine, wallet, notes);
    }

    private static async Task SyncAll(params Device[] devices)
    {
        foreach (var device in devices) Assert.Equal(SyncRunOutcome.Completed, (await device.Engine.SyncNowAsync()).Outcome);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed record Item(string Text);

    private sealed record Device(SyncEngine Engine, SyncedCollection<Item> Wallet, SyncedCollection<Item> Notes);
}
