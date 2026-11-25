using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Shell.Services;

namespace Helm.Gallery;

// In-memory stand-ins so a page runs with its real view model and no Helm around it (like tests/Helm.Tests/TestDoubles.cs).

internal sealed class MemorySynced<T> : ISyncedCollection<T> where T : class
{
    private readonly SortedDictionary<string, T> _records = new(StringComparer.Ordinal);
    private int _next;

    public string Name => typeof(T).Name;

    public event EventHandler<SyncedChangedEventArgs>? Changed;

    public T? Get(string id) => _records.GetValueOrDefault(id);

    public IReadOnlyList<SyncedItem<T>> All() =>
        _records.Select(r => new SyncedItem<T>(r.Key, r.Value, DateTimeOffset.UnixEpoch)).ToList();

    public void Upsert(string id, T value)
    {
        _records[id] = value;
        Changed?.Invoke(this, new SyncedChangedEventArgs([id], SyncChangeOrigin.Local));
    }

    public string Add(T value)
    {
        var id = (++_next).ToString("D6", CultureInfo.InvariantCulture);
        Upsert(id, value);
        return id;
    }

    public bool Delete(string id)
    {
        if (!_records.Remove(id)) return false;
        Changed?.Invoke(this, new SyncedChangedEventArgs([id], SyncChangeOrigin.Local));
        return true;
    }
}

internal sealed class MemorySyncedLog<T> : ISyncedLog<T> where T : class
{
    private readonly MemorySynced<T> _inner = new();

    public string Name => _inner.Name;

    public event EventHandler<SyncedChangedEventArgs>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public string Append(T entry) => _inner.Add(entry);

    public IReadOnlyList<SyncedItem<T>> All() => _inner.All();

    public bool Remove(string id) => _inner.Delete(id);
}

internal sealed class AvaloniaUi : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}

internal sealed class YesDialogs : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
}

internal sealed class NoClipboard : IClipboardService
{
    public void SetText(string text) { }
}

/// <summary>A module whose on/off switch the Gallery toolbar flips, to see the page's "turned off" state.</summary>
internal sealed class FakeModule(string id, string name) : IModule
{
    private bool _enabled = true;

    public string Id => id;
    public string DisplayName => name;
    public string Description => "";
    public ModuleGroup Group => ModuleGroup.Planning;
    public string? StatusMessage => null;

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public Task EnableAsync(CancellationToken ct) => Task.CompletedTask;

    public Task DisableAsync() => Task.CompletedTask;

    public event PropertyChangedEventHandler? PropertyChanged;
}
