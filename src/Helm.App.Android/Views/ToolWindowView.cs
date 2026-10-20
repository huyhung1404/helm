using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Helm.App.Android.ViewModels;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.App.Android.Views;

/// <summary>
/// A tool window's content (<see cref="ToolWindowActivity"/>): a slim bar with the tool's name, "Move to main window"
/// and "Close window", above the tool's page. A full page draws its own bar; its menu button offers the same two.
/// </summary>
internal sealed class ToolWindowView : UserControl
{
    private readonly ShellTab _tab;
    private readonly Action _moveToMain;
    private readonly Action _close;
    private TopLevel? _topLevel;

    public ToolWindowView(ShellTab tab, Action moveToMain, Action close)
    {
        _tab = tab;
        _moveToMain = moveToMain;
        _close = close;
        this.Bind(BackgroundProperty, this.GetResourceObservable("HelmBarBackground"));

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
        title.Children.Add(new ModuleIcon { Symbol = tab.Icon, Image = tab.Image, Size = 22 });
        title.Children.Add(new TextBlock { Text = tab.Title, FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 56 };
        bar.Children.Add(title);
        bar.Children.Add(BarButton(Symbol.ArrowMinimize, "Move to main window", 1, moveToMain));
        bar.Children.Add(BarButton(Symbol.Dismiss, "Close window", 2, close));
        DockPanel.SetDock(bar, Dock.Top);
        bar.IsVisible = !PageViews.IsFull(tab);

        var page = new Border
        {
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            ClipToBounds = true,
            Child = PageViews.Build(tab),
        };
        page.Bind(Border.BackgroundProperty, page.GetResourceObservable("HelmPageBackground"));

        var root = new DockPanel();
        root.Children.Add(bar);
        root.Children.Add(page);
        Content = root;
    }

    private static Button BarButton(Symbol symbol, string name, int column, Action click)
    {
        var button = new Button
        {
            Classes = { "flat" },
            Width = 48,
            Height = 48,
            Padding = default,
            Margin = new Thickness(0, 0, column == 2 ? 4 : 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new SymbolIcon { Symbol = symbol, FontSize = 22 },
        };
        ToolTip.SetTip(button, name);
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        Grid.SetColumn(button, column);
        button.Click += (_, _) => click();
        return button;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null) return;
        _topLevel.BackRequested += OnBackRequested;
        // A full page's own menu button offers what the bar would.
        ShellChrome.SetNavigation(_topLevel, ShowMenu);
        if (_topLevel.InsetsManager is { } insets)
        {
            // Edge to edge as the main window, padded by Android's safe area (status/navigation bars, cutout).
            insets.DisplayEdgeToEdgePreference = true;
            if (this.TryFindResource("HelmBarBackground", ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid)
                insets.SystemBarColor = solid.Color;
            insets.SafeAreaChanged += OnSafeAreaChanged;
            Padding = insets.SafeAreaPadding;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            if (_topLevel.InsetsManager is { } insets) insets.SafeAreaChanged -= OnSafeAreaChanged;
        }
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, Avalonia.Controls.Platform.SafeAreaChangedArgs e) => Padding = e.SafeAreaPadding;

    /// <summary>Back leaves the tool's own back stack (an open item) first; otherwise Android closes the window.</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (_tab.Page is ModulePage page && page.PageType == (page.Module as IModuleContent)?.ContentPageType
            && page.Module is Helm.Core.Platform.IBackHandler handler && handler.HandleBack())
            e.Handled = true;
    }

    private void ShowMenu(Control from)
    {
        var flyout = new MenuFlyout();
        var back = new MenuItem { Header = "Move to main window", Icon = new SymbolIcon { Symbol = Symbol.ArrowMinimize } };
        back.Click += (_, _) => _moveToMain();
        var close = new MenuItem { Header = "Close window", Icon = new SymbolIcon { Symbol = Symbol.Dismiss } };
        close.Click += (_, _) => _close();
        flyout.Items.Add(back);
        flyout.Items.Add(close);
        flyout.ShowAt(from);
    }
}
