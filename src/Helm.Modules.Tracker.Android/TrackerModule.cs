using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentIcons.Common;
using Helm.Core.Modules;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker on Android. Nothing runs in the background; the module keeps the home-screen widgets in step with the
/// data (local edits and synced changes) and with the on/off state.
/// </summary>
public sealed class TrackerModule : AndroidModuleBase, IModuleContent, IDisposable
{
    private readonly ILogger<TrackerModule> _logger;
    private readonly Timer _widgetTimer;

    public TrackerModule(TrackerStore store, ILogger<TrackerModule> logger)
    {
        _logger = logger;
        _widgetTimer = new Timer(_ => RefreshWidgets(), null, Timeout.Infinite, Timeout.Infinite);
        // A sync can change hundreds of records in a row: redraw the widgets once it has settled.
        store.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    public override string Id => TrackerIds.ModuleId;
    public override string DisplayName => TrackerIds.DisplayName;
    public override string Description => TrackerIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override Symbol Icon => Symbol.TaskListSquare;

    /// <summary>The same app icon as on Windows (Assets/tracker.png), loaded on first use.</summary>
    public override IImage? IconImage => s_logo.Value;

    private static readonly Lazy<IImage?> s_logo = new(() =>
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Helm.Modules.Tracker.Android/Assets/tracker.png"));
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // falls back to the symbol
        }
    });
    public override Type PageType => typeof(TrackerPage);
    public Type ContentPageType => typeof(TrackerContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        RefreshWidgets();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        RefreshWidgets(); // they show "Tracker is turned off"
        return Task.CompletedTask;
    }

    public void Dispose() => _widgetTimer.Dispose();

    private void RefreshWidgets()
    {
        try
        {
            TrackerWidgets.RefreshAll(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "Could not refresh the Tracker widgets");
        }
    }
}
