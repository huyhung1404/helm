using Helm.Core.Capture;
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
        // Windows registers it again through AddHelmModule; one instance either way.
        services.TryAddSingleton<NotesViewModel>();
        return services;
    }
}
