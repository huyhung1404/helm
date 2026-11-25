using Avalonia.Controls;
using Helm.Core;
using Helm.Core.Ui;
using Helm.Modules.Notes.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// <see cref="NotesView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>). It has no app bar
/// of the shell's: its menu button opens the drawer. Back closes an open note first (see <see cref="NotesModule"/>).
/// </summary>
public sealed class NotesContentPage : UserControl, IFullPage, INotesPageHost
{
    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public NotesContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<NotesModule>(), HelmAndroidServices.Current.GetRequiredService<NotesViewModel>())
    {
    }

    public NotesContentPage(NotesModule module, NotesViewModel viewModel) => Content = new NotesView(module, viewModel, this);

    public Action<Control>? OpenMenu => ShellChrome.OpenNavigation;
}
