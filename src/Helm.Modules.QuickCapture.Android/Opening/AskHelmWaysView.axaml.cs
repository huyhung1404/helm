using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Ai.Assistant;
using Helm.Core;
using Helm.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Ask Helm's "Ways to open" (<see cref="AskHelmWaysViewModel"/>), inside Quick Capture's page. It reads the phone's state
/// again whenever it is shown and whenever Helm comes back (from the "Display over other apps" page), and has the
/// edge handle follow at once.
/// </summary>
public partial class AskHelmWaysView : UserControl
{
    private readonly AskHelmWaysViewModel _viewModel;
    private readonly AskHelmOpeners _openers;

    public AskHelmWaysView()
    {
        var services = HelmAndroidServices.Current;
        _viewModel = services.GetRequiredService<AskHelmWaysViewModel>();
        _openers = services.GetRequiredService<AskHelmOpeners>();
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>Back (the arrow, or Android's back while this is shown).</summary>
    public event EventHandler? BackRequested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActivityHost.Resumed += OnResumed;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) Refresh();
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        if (IsVisible) Refresh();
    }

    private void Refresh()
    {
        _viewModel.Refresh();
        _openers.Refresh();
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
