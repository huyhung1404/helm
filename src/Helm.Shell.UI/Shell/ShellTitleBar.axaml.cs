using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Shell.Views;

/// <summary>
/// The shell's title bar (see the .axaml). View glue: the search box's keys (arrows, Enter, Escape) and its floating
/// results, and the parts a <see cref="ShellView"/> switches for its window (phone, main or torn-off window).
/// </summary>
public partial class ShellTitleBar : UserControl
{
    public ShellTitleBar()
    {
        InitializeComponent();
        TitleIcon.Source = ShellAssets.AppIcon;
        SearchBox.AddHandler(KeyDownEvent, OnSearchKey, RoutingStrategies.Tunnel);
        SearchBox.GotFocus += (_, _) => Model?.Search.Reopen();
        SearchBox.LostFocus += (_, _) => Model?.Search.CloseCommand.Execute(null);
        SearchArea.SizeChanged += (_, e) => Results.Width = e.NewSize.Width;
        MinimizeButton.Click += (_, _) => { if (Window is { } w) w.WindowState = WindowState.Minimized; };
        MaximizeButton.Click += (_, _) =>
        {
            if (Window is { } w) w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        };
        CloseButton.Click += (_, _) => Window?.Close();
    }

    private Window? _window;

    private Window? Window => TopLevel.GetTopLevel(this) as Window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = Window;
        if (_window is null) return;
        _window.PropertyChanged += OnWindowPropertyChanged;
        SetMaximized(_window.WindowState == WindowState.Maximized);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) SetMaximized(e.GetNewValue<WindowState>() == WindowState.Maximized);
    }

    private ShellViewModel? Model => DataContext as ShellViewModel;

    /// <summary>Hovering it opens a collapsed menu (<see cref="MenuPeek.Watch"/>).</summary>
    public Control MenuTrigger => Brand;

    /// <summary>The phone's "open tabs" button.</summary>
    public Button TabsButton => TabsButtonPart;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        SyncButton.IsVisible = Model?.Sync is not null;
    }

    /// <summary>Lays the bar out for its window: a phone (menu button, page name, tab count) or a wide window.</summary>
    public void Apply(bool narrow, bool mainWindow, bool windowButtons, double brandWidth, string? pageTitle, int tabCount)
    {
        Classes.Set("narrow", narrow);
        MenuButton.IsVisible = narrow && mainWindow;
        Brand.IsVisible = !narrow;
        Brand.MinWidth = mainWindow ? brandWidth : 0;
        AppTitle.Text = mainWindow || pageTitle is null ? Model?.AppName : $"{pageTitle} - {Model?.AppName}";
        PageTitle.IsVisible = narrow;
        PageTitle.Text = pageTitle;
        SearchArea.IsVisible = !narrow && mainWindow;
        TabsButtonPart.IsVisible = narrow;
        TabCount.Text = Math.Min(tabCount, 99).ToString(System.Globalization.CultureInfo.InvariantCulture);
        WindowButtons.IsVisible = windowButtons && !narrow;
    }

    /// <summary>The window is maximized: the middle button restores it.</summary>
    public void SetMaximized(bool maximized)
    {
        MaximizeGlyph.Symbol = maximized ? Symbol.SquareMultiple : Symbol.Maximize;
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
        Avalonia.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    /// <summary>Ctrl+F puts the cursor in the search box.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (Model?.Search is not { } search) return;
        switch (e.Key)
        {
            case Key.Down:
                search.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                search.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                search.ChooseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when search.IsOpen:
                search.CloseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
