using Helm.Core.Capture;
using Helm.Core.Links;
using Helm.Core.Services;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Helm.Modules.Notes;

public static class NotesCoreServices
{
    /// <summary>
    /// The platform-neutral part of Notes: its synced collection, the store, the page view model and the Quick Capture
    /// target. The platform module (Windows or Android) calls this from its own <c>AddNotesModule()</c>.
    /// </summary>
    public static IServiceCollection AddNotesCore(this IServiceCollection services)
    {
        services.AddSyncedCollection(NotesStore.Options);
        services.AddSyncGroup("notes.", NotesIds.DisplayName);
        services.AddSingleton(sp => new NotesStore(sp.GetRequiredService<ISyncedCollection<NoteItem>>()));
        services.AddSingleton<ICaptureTarget, NoteCaptureTarget>();
        // The AI agents' tools for notes (Helm's MCP server, Windows).
        services.AddSingleton<Helm.Core.Mcp.IMcpToolProvider>(sp => new NotesMcpTools(sp.GetRequiredService<NotesStore>(), sp.GetService<LinkHub>()));
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<NotesViewModel>();
        return services;
    }

    /// <summary>
    /// Notes as link targets: tasks and people in the debt book can have notes linked to them. The platform passes its
    /// Notes content page, which opens a linked note.
    /// </summary>
    public static IServiceCollection AddNoteLinks(this IServiceCollection services, Type contentPage) =>
        services.AddHelmLinks().AddSingleton<ILinkProvider>(sp => new NoteLinkProvider(sp.GetRequiredService<NotesStore>(), id =>
        {
            sp.GetRequiredService<IShellNavigation>().ShowPage(contentPage);
            sp.GetRequiredService<NotesViewModel>().OpenNote(id);
        }));
}
