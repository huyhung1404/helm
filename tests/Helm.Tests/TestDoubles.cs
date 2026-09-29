using System.Globalization;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Shell.Services;

namespace Helm.Tests;

/// <summary>An in-memory synced collection that can also play "another device" (<see cref="SetRemote"/>).</summary>
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

    /// <summary>A change that arrived from another device (null: deleted there).</summary>
    public void SetRemote(string id, T? value)
    {
        if (value is null) _records.Remove(id);
        else _records[id] = value;
        Changed?.Invoke(this, new SyncedChangedEventArgs([id], SyncChangeOrigin.Remote));
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

/// <summary>Runs posted work right away, on the calling thread.</summary>
internal sealed class InlineUi : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

internal sealed class AcceptDialogs : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
}

internal sealed class MemoryClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}

/// <summary>A folder under %TEMP% that is deleted with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
