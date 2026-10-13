namespace Helm.Core.Services;

/// <summary>
/// Something a tool keeps running while you are not looking at it (a listener, a connection, a service on the phone).
/// General → Running in background lists them, and its Stop button calls <see cref="StopAsync"/>.
/// </summary>
public sealed class BackgroundActivity : IDisposable
{
    private readonly BackgroundActivities _owner;
    private readonly Func<Task> _stop;
    private string _detail;

    internal BackgroundActivity(BackgroundActivities owner, string moduleId, string title, string detail, Func<Task> stop, DateTimeOffset since)
    {
        _owner = owner;
        _stop = stop;
        ModuleId = moduleId;
        Title = title;
        _detail = detail;
        Since = since;
    }

    public string ModuleId { get; }

    /// <summary>What runs, short: "Workspace Controller".</summary>
    public string Title { get; }

    /// <summary>What it is doing now: "Listening to Claude · 3 VS Code windows".</summary>
    public string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            _owner.RaiseChanged();
        }
    }

    public DateTimeOffset Since { get; }

    /// <summary>Stops it for good (the tool usually turns itself off). Never throws.</summary>
    public async Task StopAsync()
    {
        try
        {
            await _stop().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The owner logs its own failures; a Stop button must never crash the page.
        }
    }

    /// <summary>The owner ended it itself: it leaves the list.</summary>
    public void Dispose() => _owner.Remove(this);
}

/// <summary>The list behind General → Running in background, on both platforms.</summary>
public interface IBackgroundActivities
{
    IReadOnlyList<BackgroundActivity> Current { get; }

    /// <summary>One started, ended or changed its detail. Raised on the caller's thread.</summary>
    event EventHandler? Changed;

    /// <summary>Lists a running activity until the returned handle is disposed.</summary>
    /// <param name="stop">What the Stop button does: end it (and normally turn the tool off).</param>
    BackgroundActivity Start(string moduleId, string title, string detail, Func<Task> stop);
}

public sealed class BackgroundActivities(TimeProvider? time = null) : IBackgroundActivities
{
    private readonly object _gate = new();
    private readonly List<BackgroundActivity> _items = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IReadOnlyList<BackgroundActivity> Current
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    public event EventHandler? Changed;

    public BackgroundActivity Start(string moduleId, string title, string detail, Func<Task> stop)
    {
        var activity = new BackgroundActivity(this, moduleId, title, detail, stop, _time.GetLocalNow());
        lock (_gate) _items.Add(activity);
        RaiseChanged();
        return activity;
    }

    internal void Remove(BackgroundActivity activity)
    {
        bool removed;
        lock (_gate) removed = _items.Remove(activity);
        if (removed) RaiseChanged();
    }

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
