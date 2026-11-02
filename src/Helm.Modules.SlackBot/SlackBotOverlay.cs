using System.ComponentModel;
using System.Media;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Shows <see cref="SlackBotOverlayWindow"/> whenever there is something to do or a report to read, and hides it when
/// there is nothing left: it never goes away while an item waits. Something new opens a folded list again, so a new
/// request is seen even when the user folded it for the old ones. The window lives on the UI thread.
/// </summary>
public sealed class SlackBotOverlay
{
    private readonly SlackBotInboxViewModel _inbox;
    private readonly ISettingsStore<SlackBotSettings> _settings;
    private readonly IWindowService _windows;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<SlackBotOverlay> _logger;
    private SlackBotOverlayWindow? _window;
    private bool _running;

    public SlackBotOverlay(SlackBotInboxViewModel inbox, ISettingsStoreFactory settings, IWindowService windows, IUiDispatcher ui, ILogger<SlackBotOverlay> logger)
    {
        _inbox = inbox;
        _settings = settings.Get<SlackBotSettings>(SlackBotModule.ModuleId);
        _windows = windows;
        _ui = ui;
        _logger = logger;
        inbox.PropertyChanged += OnInboxChanged;
        inbox.NewToDo += (_, _) => OnNewToDo();
        _settings.Changed += (_, s) => _ui.Post(() =>
        {
            if (_window is not null) _window.RestOpacity = s.OverlayOpacity;
        });
    }

    /// <summary>Back to the bottom-right corner, open (the settings page).</summary>
    public void Reset()
    {
        _settings.Update(s =>
        {
            s.OverlayLeft = null;
            s.OverlayBottom = null;
            s.OverlayFolded = false;
        });
        _ui.Post(() =>
        {
            _window?.Close();
            _window = null;
            Update();
        });
    }

    public void Start()
    {
        _running = true;
        _ui.Post(Update);
    }

    public void Stop()
    {
        _running = false;
        _ui.Post(() =>
        {
            _window?.Close();
            _window = null;
        });
    }

    private void OnInboxChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlackBotInboxViewModel.HasOverlay)) Update();
    }

    private void OnNewToDo()
    {
        if (!_running) return;
        try
        {
            if (_settings.Current.PlaySound) SystemSounds.Asterisk.Play();
            if (_window is { IsFolded: true })
            {
                _window.SetFolded(false);
                _settings.Update(s => s.OverlayFolded = false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SlackBot could not show a new item");
        }
    }

    private void Update()
    {
        try
        {
            if (!_running || !_inbox.HasOverlay)
            {
                _window?.Hide();
                return;
            }
            _window ??= Create();
            _window.ShowOverlay();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SlackBot could not show its list");
        }
    }

    private SlackBotOverlayWindow Create()
    {
        var s = _settings.Current;
        var window = new SlackBotOverlayWindow(_inbox, _windows,
            s.OverlayLeft is { } left && s.OverlayBottom is { } bottom ? new System.Windows.Point(left, bottom) : null,
            s.OverlayFolded,
            at => _settings.Update(v =>
            {
                v.OverlayLeft = at?.X;
                v.OverlayBottom = at?.Y;
            }),
            folded => _settings.Update(v => v.OverlayFolded = folded));
        window.RestOpacity = s.OverlayOpacity;
        return window;
    }
}
