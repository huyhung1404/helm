using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Helm.Modules.WindowZones.Editor;

namespace Helm.Modules.WindowZones.Ui;

/// <summary>What the app does around the shared Window Zones page (all of it Windows: monitors, icons, shortcuts).</summary>
public interface IWindowZonesPageHost
{
    /// <summary>The page came on screen, or the window came back to it.</summary>
    void Opened();

    /// <summary>The connected monitors, as Windows arranges them (physical pixels).</summary>
    IReadOnlyList<MonitorInfo> Monitors();

    /// <summary>A program's icon (from its .exe), or null for the generic window glyph.</summary>
    IImage? IconFor(string processPath);

    /// <summary>
    /// A field that records a global shortcut (Helm.Ui.Windows' HotkeyBox), starting at <paramref name="gesture"/>.
    /// Null where there is none (the Gallery): the page then shows the keys only.
    /// </summary>
    Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) => null;
}

/// <summary>A window's program icon, through the page's host (set when the page is made).</summary>
public static class ZoneWindowIcons
{
    internal static Func<string, IImage?>? Provider { get; set; }

    public static IValueConverter Converter { get; } = new FuncValueConverter<string?, IImage?>(path =>
    {
        if (string.IsNullOrEmpty(path) || Provider is not { } provider) return null;
        try
        {
            return provider(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"No icon for {path}: {ex.Message}");
            return null;
        }
    });
}

/// <summary>
/// Window Zones itself (see the .axaml): the screens to scale, then a card per zone. Drawing zones is a sheet
/// (<see cref="ZonesEditorView"/>) over the page; the full-screen editor on every monitor is still the app's (Edit on the
/// screens). The settings are a sheet behind the gear, with the tool's on/off switch first. While the tool is off the
/// zones stay visible but read only.
/// </summary>
public partial class WindowZonesView : UserControl
{
    private const double Narrow = 720;
    private readonly WindowZonesViewModel? _viewModel;
    private readonly IWindowZones? _module;
    private readonly IWindowZonesPageHost? _host;
    private SheetHandle? _sheet;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public WindowZonesView() => InitializeComponent();

    public WindowZonesView(WindowZonesViewModel viewModel, IWindowZonesPageHost host)
    {
        _viewModel = viewModel;
        _module = viewModel.Module;
        _host = host;
        ZoneWindowIcons.Provider = host.IconFor;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The editor or the settings, while one is open over the page.</summary>
    public SheetHandle? OpenSheet => _sheet is { IsOpen: true } ? _sheet : null;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        FirstRunSteps.Columns = narrow ? 1 : 3;
        // The two ways to edit, one under the other on a phone.
        Grid.SetColumnSpan(EditButton, narrow ? 2 : 1);
        Grid.SetColumn(ScreensButton, narrow ? 0 : 1);
        Grid.SetRow(ScreensButton, narrow ? 1 : 0);
        Grid.SetColumnSpan(ScreensButton, narrow ? 2 : 1);
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    private void Edit_Click(object? sender, RoutedEventArgs e) => OpenEditor();

    /// <summary>The gear: Window Zones' settings as a sheet over the page.</summary>
    public SheetHandle? OpenSettings()
    {
        if (_viewModel is null || _module is null || _host is null) return null;
        SheetHandle? sheet = null;
        var view = new WindowZonesSettingsView(_viewModel, _module, _host, () => sheet?.Close());
        sheet = Sheet.Show(this, view, 520);
        _sheet = sheet;
        return sheet;
    }

    /// <summary>The zone editor as a sheet: the screens to scale, drawn on directly. Save replaces the zones.</summary>
    public SheetHandle? OpenEditor()
    {
        if (_viewModel is null || _module is null || _host is null) return null;
        var monitors = _host.Monitors();
        if (monitors.Count == 0) return null;
        var session = new EditorSession(_module.Data, monitors, monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]);
        SheetHandle? sheet = null;
        var view = new ZonesEditorView(session, _viewModel);
        session.CloseRequested += (_, _) => Dispatcher.UIThread.Post(() => sheet?.Close());
        sheet = Sheet.Show(this, view, 940);
        _sheet = sheet;
        return sheet;
    }

    /// <summary>The editor sheet's content while it is open (the Gallery and the tests drive it).</summary>
    public ZonesEditorView? Editor => (_sheet is { IsOpen: true } ? FindEditor() : null);

    private ZonesEditorView? FindEditor() =>
        Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(this) is { } layer
            ? Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(layer).OfType<ZonesEditorView>().FirstOrDefault()
            : null;

    // A zone's name is saved when its field loses focus, or on Enter.
    private void Name_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    // The module and view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _viewModel is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _module.ZonesChanged += OnZonesChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ApplyEnabled();
        Refresh();
        try
        {
            _host?.Opened();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Window Zones page check failed: {ex}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null)
        {
            _module.PropertyChanged -= OnModuleChanged;
            _module.ZonesChanged -= OnZonesChanged;
        }
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        // A sheet lives over the window, not in the page: it goes with the page.
        _sheet?.Close();
        _sheet = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnZonesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WindowZonesViewModel.Summary) or nameof(WindowZonesViewModel.DragKeyIndex)) Refresh();
    }

    /// <summary>Today's line, the overview of the screens and the drag key in the first-run steps.</summary>
    private void Refresh()
    {
        if (_viewModel is null || _module is null || _host is null) return;
        var zones = _viewModel.Zones.Count;
        var windows = _viewModel.Zones.Sum(z => z.Windows.Count);
        var day = DateTime.Now.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        TodayLine.Text = zones == 0
            ? day.ToUpperInvariant()
            : $"{day} · {Count(zones, "zone")} · {Count(windows, "window")}".ToUpperInvariant();
        StepDrop.Text = $"Hold {_viewModel.DragKeyName} while dragging a window";
        var monitors = _host.Monitors();
        Overview.Session = monitors.Count == 0 ? null : new EditorSession(_module.Data, monitors, monitors[0]);
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IModule.IsEnabled) or nameof(IModule.StatusMessage)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
        ProblemText.Text = _module.StatusMessage;
        ProblemBar.IsVisible = _module.IsEnabled && !string.IsNullOrWhiteSpace(_module.StatusMessage);
    }
}
