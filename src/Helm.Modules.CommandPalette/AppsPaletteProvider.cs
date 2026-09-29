using Helm.Core.Palette;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.CommandPalette;

/// <summary>
/// The apps in the Start menu (the shortcuts in the all-users and the user's Programs folders). Helm runs as
/// administrator, so an app is opened through Explorer: it then runs as the signed-in user, like from the Start menu.
/// The list is read in the background when the palette opens, at most every few minutes.
/// </summary>
internal sealed class AppsPaletteProvider(ISettingsStoreFactory settings, ILogger<AppsPaletteProvider> logger) : IPaletteProvider
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);
    private readonly ISettingsStore<CommandPaletteSettings> _settings = settings.Get<CommandPaletteSettings>(CommandPaletteModule.ModuleId);
    private readonly object _gate = new();
    private IReadOnlyList<StartMenuApp> _apps = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private bool _reading;

    public string? ModuleId => null;

    /// <summary>Reads the Start menu again if the list is old (in the background; results use the old list meanwhile).</summary>
    public void RefreshIfStale()
    {
        lock (_gate)
        {
            if (_reading || DateTimeOffset.UtcNow - _readAt < RefreshAfter) return;
            _reading = true;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var apps = StartMenuApps.Read();
                // Microsoft Store apps (Calculator, Photos…) have no shortcut: add them from the "All apps" list.
                var names = apps.Select(a => a.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
                var store = StartMenuApps.ReadAppsFolder(logger).Where(a => !names.Contains(a.Name));
                apps = apps.Concat(store).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                lock (_gate) _apps = apps;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read the Start menu");
            }
            finally
            {
                lock (_gate)
                {
                    _readAt = DateTimeOffset.UtcNow;
                    _reading = false;
                }
            }
        });
    }

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        if (query.IsEmpty || !_settings.Current.IncludeApps) return [];
        IReadOnlyList<StartMenuApp> apps;
        lock (_gate) apps = _apps;
        return apps
            .Select(a => (a, score: query.Score(a.Name)))
            .Where(x => x.score > 0)
            .Select(x => new PaletteItem(x.a.Name, "App", PaletteKind.App, x.score, () => ShellOpen.AsUser(x.a.Path, logger))
                { IconFile = x.a.IsShortcut ? x.a.Path : null, IsExternal = true });
    }
}

/// <param name="Path">A shortcut file, or a "shell:AppsFolder\…" name for an app from the "All apps" list.</param>
public sealed record StartMenuApp(string Name, string Path)
{
    public bool IsShortcut => !Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Start menu shortcuts, without uninstallers and duplicates.</summary>
public static class StartMenuApps
{
    private static readonly string[] Extensions = [".lnk", ".url", ".appref-ms"];

    public static IReadOnlyList<StartMenuApp> Read() => Read(
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
    ]);

    public static IReadOnlyList<StartMenuApp> Read(IEnumerable<string> folders)
    {
        var apps = new Dictionary<string, StartMenuApp>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(file).Trim();
                if (name.Length == 0 || IsNoise(name)) continue;
                // The same app in both folders: the first (all users) wins.
                apps.TryAdd(name, new StartMenuApp(name, file));
            }
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The "All apps" list of the Start menu (shell:AppsFolder), which also has Microsoft Store apps. Read on its own
    /// STA thread (Shell COM objects need one); empty when the shell cannot be reached.
    /// </summary>
    public static IReadOnlyList<StartMenuApp> ReadAppsFolder(Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var apps = new List<StartMenuApp>();
        var thread = new Thread(() =>
        {
            try
            {
                if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder is null) return;
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string path = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || IsNoise(name)) continue;
                    // Web links and documents in the list are shortcuts already read above.
                    if (path.Contains("://", StringComparison.Ordinal)) continue;
                    apps.Add(new StartMenuApp(name.Trim(), @"shell:AppsFolder\" + path));
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not read the Start menu's app list");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) return [];
        return apps.DistinctBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("gỡ cài đặt", StringComparison.OrdinalIgnoreCase)
        || name.Equals("desktop", StringComparison.OrdinalIgnoreCase);
}
