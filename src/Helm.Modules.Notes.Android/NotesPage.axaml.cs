using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Notes;

/// <summary>Notes' settings (Home → Utilities). The notes are on <see cref="NotesContentPage"/>.</summary>
public partial class NotesPage : ModulePageBase
{
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public NotesPage()
        : this(HelmAndroidServices.Current.GetRequiredService<NotesModule>(), HelmAndroidServices.Current.GetRequiredService<NotesViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public NotesPage(NotesModule module, NotesViewModel viewModel, IShellNavigation navigation)
    {
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
    }

    private void OpenNotes_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(NotesContentPage));
}
