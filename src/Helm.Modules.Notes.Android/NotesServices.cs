using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Notes;

public static class NotesServices
{
    public static IServiceCollection AddNotesModule(this IServiceCollection services) =>
        services
            .AddNotesCore()
            .AddNoteLinks(typeof(NotesContentPage))
            .AddAndroidModule<NotesModule, NotesPage>()
            .AddTransient<NotesContentPage>();
}
