using System.ComponentModel;
using System.Windows;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// Owns the Claude Chat window: one per app, created on first use. Closing it only hides it (the chats keep
/// running and still notify); turning the module off closes it and ends every chat. UI thread only.
/// </summary>
public sealed class ChatWindowHost
{
    private readonly ClaudeChatModule _module;
    private readonly ChatWorkspaceViewModel _workspace;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ChatWindowHost> _logger;
    private ChatWindow? _window;
    private bool _closingForGood;

    public ChatWindowHost(ClaudeChatModule module, ChatWorkspaceViewModel workspace, IUiDispatcher ui, ILogger<ChatWindowHost> logger)
    {
        _module = module;
        _workspace = workspace;
        _ui = ui;
        _logger = logger;
        workspace.IsWindowActive = () => _window is { IsVisible: true, IsActive: true };
        workspace.ShowRequested += (_, _) => Show();
        module.RevealRequested += (_, _) => _ui.Post(Show);
        module.PropertyChanged += OnModulePropertyChanged;
    }

    /// <summary>Opens (or brings back) the chat window on the selected chat.</summary>
    public void Show()
    {
        if (!_module.IsEnabled) return;
        try
        {
            _workspace.EnsureRestored();
            var window = EnsureWindow();
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.FocusComposer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the Claude Chat window failed");
        }
    }

    private ChatWindow EnsureWindow()
    {
        if (_window is not null) return _window;
        var settings = _module.Settings.Current;
        var window = new ChatWindow(_workspace)
        {
            Width = settings.FloatingWidth,
            Height = settings.FloatingHeight,
        };
        if (IsOnScreen(settings.WindowLeft, settings.WindowTop, settings.FloatingWidth))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = settings.WindowLeft!.Value;
            window.Top = settings.WindowTop!.Value;
        }
        window.Closing += (_, e) =>
        {
            RememberPlacement(window);
            if (_closingForGood || window.Dispatcher.HasShutdownStarted) return;
            e.Cancel = true; // hide instead: the chats keep running and come back as they were
            window.Hide();
        };
        _window = window;
        return window;
    }

    private void RememberPlacement(Window w)
    {
        if (w.WindowState != WindowState.Normal || w.ActualWidth < 100 || w.ActualHeight < 100) return;
        _module.Settings.Update(s =>
        {
            s.FloatingWidth = Math.Round(w.ActualWidth);
            s.FloatingHeight = Math.Round(w.ActualHeight);
            s.WindowLeft = Math.Round(w.Left);
            s.WindowTop = Math.Round(w.Top);
        });
    }

    /// <summary>At least the title bar of a window at this position must be on the virtual screen.</summary>
    private static bool IsOnScreen(double? left, double? top, double width)
    {
        if (left is not { } l || top is not { } t || !double.IsFinite(l) || !double.IsFinite(t)) return false;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return screen.IntersectsWith(new Rect(l, t, Math.Max(width, 100), 40));
    }

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClaudeChatModule.IsEnabled) || _module.IsEnabled) return;
        _ui.Post(() =>
        {
            if (_window is not { } window) return;
            RememberPlacement(window);
            _closingForGood = true;
            window.Close();
            _window = null;
            _closingForGood = false;
        });
    }
}
