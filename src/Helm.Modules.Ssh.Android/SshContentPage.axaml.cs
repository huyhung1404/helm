using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Modules.Ssh.Terminal;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). The terminal is a
/// WebView created while the page is shown; its height follows the visible area (it shrinks when the keyboard opens).
/// </summary>
public partial class SshContentPage : UserControl
{
    /// <summary>Space kept under the terminal for the key bar and the page's bottom margin.</summary>
    private const double BelowTerminal = 76;

    private readonly SshModule _module;
    private readonly SshViewModel _viewModel;
    private readonly IShellNavigation _navigation;
    private readonly TerminalHost _terminal;
    private ScrollViewer? _scroller;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public SshContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<SshModule>(), HelmAndroidServices.Current.GetRequiredService<SshViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>(), HelmAndroidServices.Current.GetRequiredService<IClipboardService>())
    {
    }

    public SshContentPage(SshModule module, SshViewModel viewModel, IShellNavigation navigation, IClipboardService clipboard)
    {
        _module = module;
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        _terminal = new TerminalHost(viewModel, clipboard);
        TerminalFrame.Child = _terminal;
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.TerminalFocusRequested += OnFocusRequested;
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null) _scroller.PropertyChanged += OnScrollerChanged;
        LayoutUpdated += OnLayoutUpdated;
        ApplyEnabled();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.TerminalFocusRequested -= OnFocusRequested;
        if (_scroller is not null) _scroller.PropertyChanged -= OnScrollerChanged;
        LayoutUpdated -= OnLayoutUpdated;
        _scroller = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty) FitTerminal();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => FitTerminal();

    /// <summary>The terminal fills what is visible below the picker, so the page does not scroll while typing.</summary>
    private void FitTerminal()
    {
        if (_scroller is null || !TerminalArea.IsVisible) return;
        var top = TerminalFrame.TranslatePoint(new Point(0, 0), _scroller);
        if (top is null) return;
        var height = Math.Max(220, _scroller.Bounds.Height - top.Value.Y - BelowTerminal + _scroller.Offset.Y);
        if (Math.Abs(TerminalFrame.Height - height) > 1) TerminalFrame.Height = Math.Round(height);
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_terminal.FocusTerminal, DispatcherPriority.Background);

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Picker.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e) => await ConnectAsync();

    private async void Password_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConnectAsync();
    }

    /// <summary>View glue: the password goes to this one connection and the box is emptied.</summary>
    private async Task ConnectAsync()
    {
        var password = PasswordBox.Text;
        PasswordBox.Text = "";
        try
        {
            await _viewModel.ConnectAsync(password);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // async void callers: never let an unexpected failure take Helm down.
            _viewModel.Message = "Connecting failed: " + ex.Message;
        }
    }

    private void Key_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string key) _terminal.SendKey(key);
        RefocusTerminal();
    }

    private void Char_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string text) _viewModel.Send(text);
        RefocusTerminal();
    }

    // Ctrl stays on until the next key: the keyboard goes back to the terminal for it.
    private void Ctrl_Click(object? sender, RoutedEventArgs e) => RefocusTerminal();

    /// <summary>
    /// The key bar gives the typing focus back to the terminal. Avalonia takes the focus for itself after the tap is
    /// handled, so this runs a moment later.
    /// </summary>
    private void RefocusTerminal() =>
        DispatcherTimer.RunOnce(_terminal.FocusTerminal, TimeSpan.FromMilliseconds(80));

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _terminal.PasteAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _viewModel.Message = "Pasting failed: " + ex.Message;
        }
    }

    private void Keyboard_Click(object? sender, RoutedEventArgs e) => _terminal.FocusTerminal();

    private void AddServer_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.NewHostCommand.Execute(null);
        _navigation.ShowPage(typeof(SshPage));
    }
}
