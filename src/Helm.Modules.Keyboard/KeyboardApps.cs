using Helm.Core.Sync;

namespace Helm.Modules.Keyboard;

/// <summary>The keyboard an app was last left with. Synced, so every PC remembers the same.</summary>
public sealed record KeyboardAppMode
{
    /// <summary>The app's program file name, as shown (e.g. "Code.exe").</summary>
    public required string App { get; init; }

    public KeyboardMode Mode { get; init; }
}

/// <summary>
/// The keyboard each app types with, remembered when it is left and given back when it is used again. An app never seen
/// before types English. Records are keyed by the program file name in lower case, so every PC means the same app.
/// </summary>
public sealed class KeyboardApps
{
    public const string Collection = "keyboard.apps";

    private readonly ISyncedCollection<KeyboardAppMode> _apps;

    public KeyboardApps(ISyncedCollection<KeyboardAppMode> apps)
    {
        _apps = apps;
        apps.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after a change here or from another PC (on the writing or a thread-pool thread).</summary>
    public event EventHandler? Changed;

    public KeyboardMode ModeOf(string app) => _apps.Get(Key(app))?.Mode ?? KeyboardMode.English;

    /// <summary>Remembers <paramref name="mode"/> for <paramref name="app"/>; English, the default, is forgotten.</summary>
    public void Remember(string app, KeyboardMode mode)
    {
        if (string.IsNullOrWhiteSpace(app)) return;
        var key = Key(app);
        var known = _apps.Get(key);
        if (mode == KeyboardMode.English)
        {
            if (known is not null) _apps.Delete(key);
            return;
        }
        if (known?.Mode == mode) return;
        _apps.Upsert(key, new KeyboardAppMode { App = app, Mode = mode });
    }

    public void Forget(string app) => _apps.Delete(Key(app));

    /// <summary>Every app with a keyboard of its own, by name.</summary>
    public IReadOnlyList<KeyboardAppMode> All() =>
        _apps.All().Select(item => item.Value).OrderBy(app => app.App, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Key(string app) => app.Trim().ToLowerInvariant();
}
