using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Helm.App.Android.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.Views;

public partial class MainView : UserControl
{
    private IInsetsManager? _insets;
    private TopLevel? _topLevel;

    public MainView()
    {
        InitializeComponent();
        // A tool's page is whatever control its module names (created fresh from DI each time it is shown).
        DataTemplates.Add(new FuncDataTemplate<ModulePage>((page, _) => (Control)App.Services.GetRequiredService(page.Module.PageType)));
        PageHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty) PageScroller.Offset = default;
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null) return;

        // Android 15+ always draws edge to edge; pad the shell by the status/navigation bars and display cutout.
        _insets = _topLevel.InsetsManager;
        if (_insets is not null)
        {
            _insets.DisplayEdgeToEdgePreference = true;
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            ContentInsets.Changed += OnContentInsetsChanged;
            UpdatePadding();
            UpdateSystemBarColor();
        }
        ActualThemeVariantChanged += OnThemeChanged;
        _topLevel.BackRequested += OnBackRequested;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null) _insets.SafeAreaChanged -= OnSafeAreaChanged;
        ContentInsets.Changed -= OnContentInsetsChanged;
        if (_topLevel is not null) _topLevel.BackRequested -= OnBackRequested;
        ActualThemeVariantChanged -= OnThemeChanged;
        _insets = null;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateSystemBarColor();

    /// <summary>Status/navigation bars take the app bar colour (Avalonia picks light or dark bar icons to match).</summary>
    private void UpdateSystemBarColor()
    {
        if (_insets is not null && this.TryFindResource("HelmBarBackground", ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid)
            _insets.SystemBarColor = solid.Color;
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => UpdatePadding();

    private void OnContentInsetsChanged(object? sender, EventArgs e) => UpdatePadding();

    /// <summary>Pads the shell clear of the status/navigation bars and cutout, once (see <see cref="ContentInsets"/>).</summary>
    private void UpdatePadding()
    {
        if (_insets is not null) Padding = ContentInsets.Remaining(_insets.SafeAreaPadding);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.HandleBack()) e.Handled = true;
    }
}
