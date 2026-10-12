namespace Helm.Modules.ShortcutGuide;

/// <summary>
/// What the library page changes, applied to the settings. Built-in shortcuts are never changed in place: an edit is
/// one of yours that <see cref="UserShortcut.Replaces"/> it, and deleting one hides it, so both can be undone. Pure
/// logic on the settings object; the view model runs it inside <c>store.Update</c>.
/// </summary>
public static class ShortcutEdits
{
    /// <summary>Why <paramref name="keys"/> cannot be used for <paramref name="original"/> (or a new one when null), or null when they can.</summary>
    public static string? Check(ShortcutGuideSettings settings, string appId, GuideShortcut? original, ShortcutKeys keys, string does)
    {
        if (does.Trim().Length == 0) return "Say what the shortcut does.";
        var clash = settings.Shortcuts.FirstOrDefault(u => Same(u.AppId, appId) && Canonical(u.Keys) is { } c && Same(c, keys.Canonical));
        if (clash is null || original is { IsOwn: true } && Same(Canonical(clash.Keys), original.Keys.Canonical)) return null;
        // Adding the same keys again just changes what they do; editing another shortcut onto them would lose one.
        return original is null ? null : $"{keys} is already one of your shortcuts here.";
    }

    /// <summary>Adds a shortcut of yours. The same keys again change what they do.</summary>
    public static void Add(ShortcutGuideSettings settings, string appId, ShortcutKeys keys, string does, bool basic)
    {
        var replaces = settings.Shortcuts.FirstOrDefault(u => Same(u.AppId, appId) && Same(Canonical(u.Keys), keys.Canonical))?.Replaces;
        settings.Shortcuts.RemoveAll(u => Same(u.AppId, appId) && Same(Canonical(u.Keys), keys.Canonical));
        settings.Shortcuts.Add(new UserShortcut { AppId = appId, Keys = keys.Canonical, Does = does.Trim(), Basic = basic, Replaces = replaces });
    }

    /// <summary>Changes the keys, what it does or whether it is basic. A built-in shortcut becomes your edit of it.</summary>
    public static void Save(ShortcutGuideSettings settings, GuideShortcut original, ShortcutKeys keys, string does, bool basic)
    {
        if (original.IsOwn && Find(settings, original) is { } mine)
        {
            mine.Keys = keys.Canonical;
            mine.Does = does.Trim();
            mine.Basic = basic;
            return;
        }
        settings.Shortcuts.RemoveAll(u => Same(u.AppId, original.AppId) && Same(Canonical(u.Keys), keys.Canonical));
        settings.Shortcuts.Add(new UserShortcut
        {
            AppId = original.AppId,
            Keys = keys.Canonical,
            Does = does.Trim(),
            Basic = basic,
            Replaces = original.Keys.Canonical,
        });
    }

    /// <summary>Yours goes for good; a built-in one (or your edit of it) is hidden and can be restored.</summary>
    public static void Delete(ShortcutGuideSettings settings, GuideShortcut shortcut)
    {
        if (shortcut.IsOwn)
        {
            if (Find(settings, shortcut) is { } mine) settings.Shortcuts.Remove(mine);
            if (shortcut.Replaces is { } builtIn) Hide(settings, shortcut.AppId, builtIn);
            return;
        }
        Hide(settings, shortcut.AppId, shortcut.Keys.Canonical);
    }

    /// <summary>A deleted built-in shortcut comes back.</summary>
    public static void Restore(ShortcutGuideSettings settings, GuideShortcut shortcut)
    {
        var key = HiddenKey(shortcut.AppId, shortcut.Keys.Canonical);
        settings.Hidden.RemoveAll(h => Same(h, key));
    }

    /// <summary>Drops your edit of a built-in shortcut: the original comes back.</summary>
    public static void Reset(ShortcutGuideSettings settings, GuideShortcut shortcut)
    {
        if (shortcut is { IsOwn: true, IsEdited: true } && Find(settings, shortcut) is { } mine) settings.Shortcuts.Remove(mine);
    }

    private static UserShortcut? Find(ShortcutGuideSettings settings, GuideShortcut shortcut) =>
        settings.Shortcuts.FirstOrDefault(u => Same(u.AppId, shortcut.AppId) && Same(Canonical(u.Keys), shortcut.Keys.Canonical));

    private static void Hide(ShortcutGuideSettings settings, string appId, string canonical)
    {
        var key = HiddenKey(appId, canonical);
        if (!settings.Hidden.Any(h => Same(h, key))) settings.Hidden.Add(key);
    }

    private static string HiddenKey(string appId, string canonical) => $"{appId}|{canonical}";

    private static string? Canonical(string keys) => ShortcutKeys.TryParse(keys, out var parsed, out _) ? parsed.Canonical : null;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
