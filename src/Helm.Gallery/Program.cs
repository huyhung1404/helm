using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Helm.Core.Ui;

namespace Helm.Gallery;

internal sealed class GalleryApp : Application
{
    public override void Initialize() =>
        Styles.Add(new StyleInclude(new Uri("avares://Helm.Gallery/")) { Source = new Uri("avares://Helm.Ui/HelmTheme.axaml") });

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new GalleryWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Pages on the left; frame size, theme and the tool's on/off switch on top; the page in a frame.</summary>
internal sealed class GalleryWindow : Window
{
    private readonly ListBox _pages = new() { ItemsSource = GalleryPage.All, DisplayMemberBinding = new Avalonia.Data.Binding("Name") };
    private readonly ComboBox _size = new() { ItemsSource = new[] { "Phone 390", "Tablet 800", "PC (fill)" }, SelectedIndex = 2 };
    private readonly ToggleSwitch _dark = new() { OnContent = "Dark", OffContent = "Light" };
    private readonly ToggleSwitch _on = new() { OnContent = "Tool on", OffContent = "Tool off", IsChecked = true };
    private readonly Border _frame = new() { ClipToBounds = true, CornerRadius = new CornerRadius(8) };
    private readonly ThemeVariantScope _scope = new() { RequestedThemeVariant = ThemeVariant.Light };
    private readonly FakeModule _module = new("tracker", "Tracker");

    public GalleryWindow()
    {
        Title = "Helm Gallery";
        Width = 1280;
        Height = 860;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 8) };
        bar.Children.Add(_size);
        bar.Children.Add(_dark);
        bar.Children.Add(_on);

        _frame.Child = _scope;
        var stage = new Border { Padding = new Thickness(16), Background = Avalonia.Media.Brushes.Gainsboro, Child = _frame };
        var side = new Border { Width = 220, Padding = new Thickness(8), Child = _pages };

        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(side);
        dock.Children.Add(bar);
        dock.Children.Add(stage);
        Content = dock;

        _pages.SelectionChanged += (_, _) => ShowPage();
        _size.SelectionChanged += (_, _) => ApplySize();
        _dark.IsCheckedChanged += (_, _) => _scope.RequestedThemeVariant = _dark.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
        _on.IsCheckedChanged += (_, _) => _module.IsEnabled = _on.IsChecked == true;
        _pages.SelectedIndex = 0;
        ApplySize();
        Opened += (_, _) => Helm.Ui.Windows.InputLog.Attach(this, "gallery");
    }

    private void ShowPage()
    {
        if (_pages.SelectedItem is GalleryPage page) _scope.Child = new PageFrame("Tracker", page.Create(_module));
    }

    private void ApplySize()
    {
        var (w, h) = _size.SelectedIndex switch { 0 => (390d, 780d), 1 => (800d, 760d), _ => (double.NaN, double.NaN) };
        _frame.Width = w;
        _frame.Height = h;
        _frame.HorizontalAlignment = double.IsNaN(w) ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _frame.VerticalAlignment = double.IsNaN(h) ? VerticalAlignment.Stretch : VerticalAlignment.Top;
    }
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<GalleryApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
