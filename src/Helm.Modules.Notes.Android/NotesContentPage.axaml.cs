using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the notes stay visible but read-only, with a note pointing to the settings.
/// </summary>
public partial class NotesContentPage : UserControl
{
    private readonly NotesModule _module;
    private readonly NotesViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public NotesContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<NotesModule>(), HelmAndroidServices.Current.GetRequiredService<NotesViewModel>())
    {
    }

    public NotesContentPage(NotesModule module, NotesViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.EditorFocusRequested += OnFocusRequested;
        ApplyEnabled();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.EditorFocusRequested -= OnFocusRequested;
        // Leaving the page (another tool, Home) saves what was typed.
        _viewModel.Flush();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => BodyBox.Focus(), DispatcherPriority.Background);

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
