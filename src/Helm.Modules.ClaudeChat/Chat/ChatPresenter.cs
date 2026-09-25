using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// Decides where the one <see cref="ClaudeChatView"/> lives: on the Claude Chat page (docked, the default) or in a
/// <see cref="ChatWindow"/> after the user drags its header out. The view — and so the running conversation — is
/// moved, never recreated. UI thread only.
/// </summary>
public sealed partial class ChatPresenter : ObservableObject
{
    private readonly ClaudeChatView _view;
    private readonly ClaudeChatModule _module;
    private readonly IWindowService _windows;
    private readonly IShellNavigation _navigation;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ChatPresenter> _logger;
    private ContentControl? _pageHost;
    private ChatWindow? _window;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DockCommand), nameof(ShowWindowCommand))]
    private bool _isDetached;

    public ChatPresenter(ClaudeChatView view, ClaudeChatModule module, IWindowService windows, IShellNavigation navigation, IUiDispatcher ui, ILogger<ChatPresenter> logger)
    {
        _view = view;
        _module = module;
        _windows = windows;
        _navigation = navigation;
        _ui = ui;
        _logger = logger;
        _view.TearOffRequested += (_, grab) => Detach(grab);
        _view.PopOutRequested += (_, _) => PopOut();
        _view.WindowDragRequested += (_, _) => DragWindow();
        module.RevealRequested += (_, _) => _ui.Post(Reveal);
        module.PropertyChanged += OnModulePropertyChanged;
    }

    /// <summary>Called once by the page: where the view goes when docked.</summary>
    public void AttachPageHost(ContentControl host)
    {
        _pageHost = host;
        if (!IsDetached) Place(host);
    }

    /// <summary>Takes the view out of the page into a floating window under the cursor and keeps dragging it.</summary>
    /// <param name="grab">Where the header was grabbed, relative to the view, in DIPs.</param>
    public void Detach(Point grab) => DetachCore(grab);

    /// <summary>Opens the floating window over where the chat was on the page, without a drag.</summary>
    public void PopOut() => DetachCore(null);

    private void DetachCore(Point? grab)
    {
        if (IsDetached || !_module.IsEnabled) return;
        try
        {
            // Where the view is now (physical pixels), before it leaves the page.
            PixelPoint? viewOrigin = _view.IsVisible ? ToPixelPoint(_view.PointToScreen(new Point(0, 0))) : null;
            var window = EnsureWindow();
            var settings = _module.Settings.Current;
            window.Width = settings.FloatingWidth;
            window.Height = settings.FloatingHeight;
            Place(window);
            IsDetached = true;

            // Show it off-screen first so it has a handle and a DPI, then put the grab point under the cursor.
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = (int)Math.Round(window.Width * dpi.DpiScaleX);
            var height = (int)Math.Round(window.Height * dpi.DpiScaleY);
            // The grabbed point (or the view's corner) stays where it was: 8 = the host's margin in ChatWindow.xaml.
            var inView = grab ?? new Point(0, 0);
            var offsetX = (int)Math.Round((inView.X + 8) * dpi.DpiScaleX);
            var offsetY = (int)Math.Round((inView.Y + window.ViewTop) * dpi.DpiScaleY);
            // After a drag the grab point follows the cursor; a pop-out keeps the view's corner where it was.
            var anchor = grab is null && viewOrigin is { } origin
                ? new PixelPoint(origin.X + offsetX, origin.Y + offsetY)
                : _windows.GetCursorPosition();
            _windows.SetWindowBounds(hwnd, PixelRect.FromSize(anchor.X - offsetX, anchor.Y - offsetY, width, height));
            window.Activate();
            _view.FocusComposer();

            // After a drag, keep following the mouse as if the user had grabbed the new window's title bar.
            if (grab is not null && Mouse.LeftButton == MouseButtonState.Pressed) window.Dispatcher.BeginInvoke(DispatcherPriority.Input, DragWindow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tearing the chat out into its own window failed");
            Dock();
        }
    }

    /// <summary>Puts the view back on the page and hides the floating window.</summary>
    [RelayCommand(CanExecute = nameof(IsDetached))]
    public void Dock()
    {
        if (_window is { } window)
        {
            RememberSize(window);
            window.View = null;
            window.Hide();
        }
        IsDetached = false;
        if (_pageHost is not null) Place(_pageHost);
    }

    [RelayCommand(CanExecute = nameof(IsDetached))]
    private void ShowWindow()
    {
        if (_window is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        _view.FocusComposer();
    }

    /// <summary>The hotkey: bring up the chat wherever it is.</summary>
    public void Reveal()
    {
        if (!_module.IsEnabled) return;
        if (IsDetached)
        {
            ShowWindow();
            return;
        }
        _navigation.ShowPage(typeof(ClaudeChatPage));
        _ui.Post(_view.FocusComposer);
    }

    private void DragWindow()
    {
        if (_window is not { IsVisible: true } window || Mouse.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag loop started.
        }
    }

    private static PixelPoint ToPixelPoint(Point p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));

    private ChatWindow EnsureWindow()
    {
        if (_window is not null) return _window;
        _window = new ChatWindow();
        _window.DockRequested += (_, _) => Dock();
        return _window;
    }

    /// <summary>Moves the single view into <paramref name="host"/>, taking it out of wherever it was.</summary>
    private void Place(object host)
    {
        // Both hosts hold the view in a ContentControl (the page's ChatHost, the window's Host), which is its
        // logical parent; a WPF element can only have one.
        if (_view.Parent is ContentControl current) current.Content = null;
        if (host is ChatWindow window)
        {
            window.View = _view;
            _view.IsFloating = true;
        }
        else if (host is ContentControl control)
        {
            control.Content = _view;
            _view.IsFloating = false;
        }
    }

    private void RememberSize(Window window)
    {
        if (window.WindowState != WindowState.Normal || window.ActualWidth < 100 || window.ActualHeight < 100) return;
        _module.Settings.Update(s =>
        {
            s.FloatingWidth = Math.Round(window.ActualWidth);
            s.FloatingHeight = Math.Round(window.ActualHeight);
        });
    }

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClaudeChatModule.IsEnabled) || _module.IsEnabled) return;
        _ui.Post(() =>
        {
            if (IsDetached) Dock(); // turning the module off puts the chat back where its toggle is
        });
    }
}
