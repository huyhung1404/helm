using Android.Content;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary on Android. While Helm's process runs, the screen turning on puts a new word on the lock screen
/// (<see cref="VocabLockScreen"/>) and every unlock a new word on the home-screen widgets (<see cref="VocabUnlockReceiver"/>;
/// Android delivers both only to a receiver registered at run time). Both follow the data (an analysis arriving, answers
/// from the PC) and the on/off state.
/// </summary>
public sealed class VocabModule : AndroidModuleBase, IModuleContent, IDisposable
{
    private readonly ILogger<VocabModule> _logger;
    private readonly Timer _widgetTimer;
    private VocabUnlockReceiver? _unlock;

    public VocabModule(VocabStore store, ILogger<VocabModule> logger)
    {
        _logger = logger;
        _widgetTimer = new Timer(_ => RefreshWidgets(), null, Timeout.Infinite, Timeout.Infinite);
        // A sync can change many records in a row: redraw the widgets once it has settled.
        store.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    public override string Id => VocabIds.ModuleId;
    public override string DisplayName => VocabIds.DisplayName;
    public override string Description => VocabIds.Description;
    public override ModuleGroup Group => ModuleGroup.Learning;
    public override Symbol Icon => Symbol.BookLetter;

    /// <summary>The same vector icon as on Windows (<see cref="VocabIconShape"/>).</summary>
    public override IImage? IconImage => VocabIcon.Image;

    public override Type PageType => typeof(VocabPage);
    public Type ContentPageType => typeof(VocabContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        try
        {
            if (_unlock is null)
            {
                _unlock = new VocabUnlockReceiver();
                var filter = new IntentFilter(Intent.ActionUserPresent);
                filter.AddAction(Intent.ActionScreenOn);
                AndroidApp.Context.RegisterReceiver(_unlock, filter);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not listen for the phone being unlocked");
        }
        RefreshWidgets();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        try
        {
            if (_unlock is not null) AndroidApp.Context.UnregisterReceiver(_unlock);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop listening for unlocks");
        }
        _unlock = null;
        RefreshWidgets(); // they show "Vocabulary is turned off"
        VocabLockScreen.Cancel(AndroidApp.Context);
        return Task.CompletedTask;
    }

    public void Dispose() => _widgetTimer.Dispose();

    private void RefreshWidgets()
    {
        try
        {
            VocabWidgets.RefreshAll(AndroidApp.Context);
            VocabLockScreen.Refresh(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "Could not refresh the Vocabulary widgets");
        }
    }
}

/// <summary>The screen turned on: a new word on the lock screen. The phone was unlocked: a new word on every widget.</summary>
public sealed class VocabUnlockReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        if (intent?.Action == Intent.ActionScreenOn) VocabLockScreen.NextWordIfIdle(context);
        else if (intent?.Action == Intent.ActionUserPresent) VocabWidgets.NextWordAll(context);
    }
}
