using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Styling;
using Helm.Core.Ui;

namespace Helm.Gallery;

internal sealed class GalleryApp : Application
{
    public override void Initialize() => Styles.Add(new HelmTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new GalleryWindow();
            desktop.MainWindow = window;
            if (Shots.Value("--shot") is { } folder)
            {
                // Off screen and out of the way: nothing to look at, nothing to take the focus.
                window.ShowActivated = false;
                window.Position = new PixelPoint(-4000, 40);
                window.Opened += (_, _) => _ = window.ShootAsync(folder, Shots.Value("--only"));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Pages on the left; frame size and theme on top; the page in a frame the size of the chosen screen.</summary>
internal sealed class GalleryWindow : Window
{
    private readonly ListBox _pages = new() { ItemsSource = GalleryPage.All, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(GalleryPage.Label)) };
    private readonly ComboBox _size = new() { ItemsSource = new[] { "Phone 390", "Tablet 800", "PC (fill)" }, SelectedIndex = 2 };
    private readonly ToggleSwitch _dark = new() { OnContent = "Dark", OffContent = "Light", IsChecked = true };
    private readonly ToggleSwitch _on = new() { OnContent = "Tool on", OffContent = "Tool off", IsChecked = true };
    private readonly FakeModule _module = new("tool", "Tool");
    // "screen": what a page's sheets and pickers open over, so the phone frame behaves like a phone.
    private readonly Border _frame = new() { ClipToBounds = true, CornerRadius = new CornerRadius(8), Classes = { "screen" } };
    private readonly ThemeVariantScope _scope = new() { RequestedThemeVariant = ThemeVariant.Dark };

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
        var side = new Border { Width = 240, Padding = new Thickness(8), Child = _pages };

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
    }

    private void ShowPage()
    {
        if (_pages.SelectedItem is not GalleryPage page) return;
        try
        {
            var view = page.Create(_module);
            _scope.Child = page.Framed ? new PageFrame(page.Title, view) : view;
        }
        catch (Exception ex)
        {
            // A page that throws while being built shows why instead of closing the Gallery.
            _scope.Child = new PageFrame(page.Title, new SelectableTextBlock { Text = ex.ToString(), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }
    }

    private void ApplySize()
    {
        var (w, h) = _size.SelectedIndex switch { 0 => (390d, 780d), 1 => (800d, 760d), _ => (double.NaN, double.NaN) };
        _frame.Width = w;
        _frame.Height = h;
        _frame.HorizontalAlignment = double.IsNaN(w) ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _frame.VerticalAlignment = double.IsNaN(h) ? VerticalAlignment.Stretch : VerticalAlignment.Top;
    }

    /// <summary>
    /// <c>--shot &lt;folder&gt; [--only &lt;text&gt;]</c>: saves every page (or those whose label contains the text) at phone and
    /// PC size, dark and light, as PNGs (the whole window, so sheets show too), then closes. For checking a page without
    /// looking at the screen.
    /// </summary>
    public async Task ShootAsync(string folder, string? only)
    {
        Directory.CreateDirectory(folder);
        foreach (var page in GalleryPage.All.Where(p => only is null || p.Label.Contains(only, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var (size, sizeName) in new[] { (0, "phone"), (2, "pc") })
            {
                foreach (var dark in new[] { true, false })
                {
                    _size.SelectedIndex = size;
                    _dark.IsChecked = dark;
                    _pages.SelectedItem = null;
                    _pages.SelectedItem = page;
                    // Pages and sheets grow into place (260 ms); confetti falls for about three seconds.
                    await Task.Delay(page.Name == "Celebration" ? 900 : 1200);
                    var pixels = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(pixels);
                    bitmap.Render(this);
                    var name = string.Concat($"{page.Group}-{page.Name}-{sizeName}-{(dark ? "dark" : "light")}".Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
                    bitmap.Save(Path.Combine(folder, name + ".png"));
                }
            }
        }
        Close();
    }
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<GalleryApp>().UsePlatformDetect().AfterSetup(_ => Shots.Args = args).StartWithClassicDesktopLifetime(args);
}

/// <summary>The <c>--shot</c> mode's arguments (see <see cref="GalleryWindow.ShootAsync"/>).</summary>
internal static class Shots
{
    public static string[] Args { get; set; } = [];

    public static string? Value(string name) => Array.IndexOf(Args, name) is var i and >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
}
