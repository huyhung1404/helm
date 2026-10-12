using System.Text.Json;
using Helm.Core.Settings;

namespace Helm.Modules.ShortcutGuide;

/// <summary>One app in the built-in catalog (ShortcutCatalog.json).</summary>
public sealed class CatalogApp
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Process names without ".exe", e.g. "Code" and "Cursor" for VS Code. Empty for Windows and Most apps.</summary>
    public List<string> Processes { get; set; } = [];

    public List<CatalogShortcut> Shortcuts { get; set; } = [];
}

public sealed class CatalogShortcut
{
    public string Keys { get; set; } = string.Empty;

    public string Does { get; set; } = string.Empty;

    /// <summary>Everyone knows it (copy, paste, undo …): the guide skips it while "Skip the basics" is on.</summary>
    public bool Basic { get; set; }
}

/// <summary>A shortcut as the guide and the library page show it.</summary>
public sealed record GuideShortcut(string AppId, ShortcutKeys Keys, string Does, bool IsOwn, bool IsHidden, bool IsBasic = false, string? Replaces = null)
{
    /// <summary>Your edit of a built-in shortcut (Reset brings the original back).</summary>
    public bool IsEdited => Replaces is not null;
}

/// <summary>An app with its effective shortcuts: the catalog's, minus those you hid, plus yours.</summary>
public sealed record GuideApp(string Id, string Name, IReadOnlyList<string> Processes, bool IsOwn, IReadOnlyList<GuideShortcut> Shortcuts)
{
    /// <summary>Windows itself and "Most apps": always there, not tied to a process.</summary>
    public bool IsGeneral => Id is ShortcutLibrary.WindowsId or ShortcutLibrary.CommonId;

    /// <summary>The shortcuts the guide shows (not hidden).</summary>
    public IEnumerable<GuideShortcut> Visible => Shortcuts.Where(s => !s.IsHidden);
}

/// <summary>
/// The built-in catalog merged with what you changed (your apps, your shortcuts, the ones you hid). Immutable: a new
/// one is built whenever the settings change.
/// </summary>
public sealed class ShortcutLibrary
{
    public const string WindowsId = "windows", CommonId = "common";

    private readonly Dictionary<string, GuideApp> _byId;
    private readonly Dictionary<string, GuideApp> _byProcess;

    public ShortcutLibrary(IReadOnlyList<CatalogApp> catalog, ShortcutGuideSettings settings)
    {
        var hidden = new HashSet<string>(settings.Hidden, StringComparer.OrdinalIgnoreCase);
        var own = settings.Shortcuts
            .Select(s => ShortcutKeys.TryParse(s.Keys, out var keys, out _) ? (s.AppId, Keys: keys, s.Does, s.Basic, s.Replaces) : default)
            .Where(s => s.Keys is not null && s.AppId.Length > 0)
            .GroupBy(s => s.AppId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        GuideApp Build(string id, string name, IEnumerable<string> processes, bool isOwn, IEnumerable<CatalogShortcut> builtIn)
        {
            var mine = own.TryGetValue(id, out var list) ? list : [];
            var placed = new HashSet<int>();
            var shortcuts = new List<GuideShortcut>();
            GuideShortcut Mine(int i) => new(id, mine[i].Keys, mine[i].Does, IsOwn: true, IsHidden: false, mine[i].Basic, mine[i].Replaces);
            foreach (var s in builtIn)
            {
                if (!ShortcutKeys.TryParse(s.Keys, out var keys, out _)) continue;
                // Yours replaces the built-in shortcut it is an edit of, or the one with the same keys, in its place.
                var index = mine.FindIndex(m => string.Equals(m.Replaces, keys.Canonical, StringComparison.OrdinalIgnoreCase));
                if (index < 0) index = mine.FindIndex(m => m.Replaces is null && string.Equals(m.Keys.Canonical, keys.Canonical, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    if (placed.Add(index)) shortcuts.Add(Mine(index));
                    continue;
                }
                shortcuts.Add(new GuideShortcut(id, keys, s.Does, IsOwn: false, hidden.Contains(HiddenKey(id, keys)), s.Basic));
            }
            for (var i = 0; i < mine.Count; i++)
                if (!placed.Contains(i)) shortcuts.Add(Mine(i));
            return new GuideApp(id, name, processes.Select(NormalizeProcess).Where(p => p.Length > 0).ToList(), isOwn, shortcuts);
        }

        var apps = new List<GuideApp>();
        foreach (var app in catalog)
            apps.Add(Build(app.Id, app.Name, app.Processes, isOwn: false, app.Shortcuts));
        foreach (var app in settings.Apps.Where(a => a.Id.Length > 0 && a.Name.Length > 0))
        {
            if (apps.Any(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase))) continue;
            apps.Add(Build(app.Id, app.Name, app.Processes, isOwn: true, []));
        }

        _byId = apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        _byProcess = new Dictionary<string, GuideApp>(StringComparer.OrdinalIgnoreCase);
        // Your apps win over the catalog for the same process.
        foreach (var app in apps.OrderByDescending(a => a.IsOwn))
            foreach (var process in app.Processes)
                _byProcess.TryAdd(process, app);

        Windows = _byId.TryGetValue(WindowsId, out var windows) ? windows : new GuideApp(WindowsId, "Windows", [], false, []);
        Common = _byId.TryGetValue(CommonId, out var common) ? common : new GuideApp(CommonId, "Most apps", [], false, []);
        // Windows and Most apps first, then the apps by name.
        Apps = [Windows, Common, .. apps.Where(a => !a.IsGeneral).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public GuideApp Windows { get; }

    public GuideApp Common { get; }

    public IReadOnlyList<GuideApp> Apps { get; }

    public GuideApp? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The app for a process name ("Code.exe" or "Code"), or null when no app lists it.</summary>
    public GuideApp? FindByProcess(string? processName) =>
        string.IsNullOrEmpty(processName) ? null : _byProcess.GetValueOrDefault(NormalizeProcess(processName));

    /// <summary>How a hidden built-in shortcut is stored in <see cref="ShortcutGuideSettings.Hidden"/>.</summary>
    public static string HiddenKey(string appId, ShortcutKeys keys) => $"{appId}|{keys.Canonical}";

    /// <summary>"Code.exe" → "Code".</summary>
    public static string NormalizeProcess(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>The catalog that ships with Helm (an embedded resource).</summary>
    public static IReadOnlyList<CatalogApp> LoadBuiltInCatalog()
    {
        using var stream = typeof(ShortcutLibrary).Assembly.GetManifestResourceStream("Helm.Modules.ShortcutGuide.ShortcutCatalog.json")
            ?? throw new InvalidOperationException("The shortcut catalog is missing from the build.");
        return ParseCatalog(stream);
    }

    public static IReadOnlyList<CatalogApp> ParseCatalog(Stream json) =>
        JsonSerializer.Deserialize<CatalogFile>(json, HelmJson.Options)?.Apps ?? [];

    private sealed class CatalogFile
    {
        public List<CatalogApp> Apps { get; set; } = [];
    }
}
