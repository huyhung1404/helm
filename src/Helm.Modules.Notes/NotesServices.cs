using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Notes;

public static class NotesServices
{
    public static IServiceCollection AddNotesModule(this IServiceCollection services) =>
        services
            .AddNotesCore()
            .AddNoteLinks(typeof(NotesContentPage))
            .AddHelmModule<NotesModule, NotesPage, NotesViewModel>()
            .AddSingleton<NotesContentPage>();
}
