using System.Diagnostics;
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
            .Select(x => new PaletteItem(x.a.Name, "App", PaletteKind.App, x.score * 0.95, () => Open(x.a)) { IconFile = x.a.Path });
    }

    private void Open(StartMenuApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{app.Path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {App}", app.Path);
        }
    }
}

public sealed record StartMenuApp(string Name, string Path);

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

    private static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("gỡ cài đặt", StringComparison.OrdinalIgnoreCase)
        || name.Equals("desktop", StringComparison.OrdinalIgnoreCase);
}
