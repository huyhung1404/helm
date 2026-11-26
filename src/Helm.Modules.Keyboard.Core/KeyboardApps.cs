using Helm.Core.Sync;

namespace Helm.Modules.Keyboard;

/// <summary>The keyboard an app, or a web site, was last left with. Synced, so every PC remembers the same.</summary>
public sealed record KeyboardAppMode
{
    /// <summary>The app's program file name ("Code.exe"), or a web site ("web:facebook.com"), as shown.</summary>
    public required string App { get; init; }

    public KeyboardMode Mode { get; init; }
}

/// <summary>
/// The keyboard each app — and each web site a browser shows — types with, remembered when it is left and given back
/// when it is used again. A browser tab is keyed by its site, so Facebook and Bilibili keep their own keyboard in the
/// one browser; everything else is keyed by its program file name. Records are keyed in lower case, so every PC means
/// the same app or site. An app or site never seen before types English.
/// </summary>
public sealed class KeyboardApps
{
    public const string Collection = "keyboard.apps";

    private const string SitePrefix = "web:";

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

    /// <summary>Whether <paramref name="site"/> has a keyboard remembered for it (as opposed to never seen).</summary>
    public bool Knows(string? site) => site is not null && _apps.Get(Key(SiteKeyOf(site))) is not null;

    /// <summary>The storage name for a web site ("facebook.com" → "web:facebook.com").</summary>
    public static string SiteKeyOf(string site) => SitePrefix + site.Trim().ToLowerInvariant();

    /// <summary>Every app and site with a keyboard of its own, by name.</summary>
    public IReadOnlyList<KeyboardAppMode> All() =>
        _apps.All().Select(item => item.Value).OrderBy(app => app.App, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Key(string app) => app.Trim().ToLowerInvariant();
}
