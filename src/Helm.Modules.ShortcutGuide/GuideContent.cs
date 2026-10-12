using Helm.Core.Hotkeys;

namespace Helm.Modules.ShortcutGuide;

/// <summary>One line of the guide: what is left to press after the held modifiers, and what it does.</summary>
public sealed record GuideItem(IReadOnlyList<IReadOnlyList<string>> Keys, string Does);

public sealed record GuideSection(string Title, IReadOnlyList<GuideItem> Items);

/// <summary>"Add Shift for 12 more": another modifier you could hold for more shortcuts.</summary>
public sealed record GuideHint(HotkeyModifiers Add, int Count)
{
    public IReadOnlyList<string> Caps => ShortcutStep.ModifierCaps(Add);
}

/// <summary>What the overlay shows for one app and one set of held modifiers.</summary>
public sealed record GuideView(
    string AppName,
    bool IsKnownApp,
    HotkeyModifiers Held,
    IReadOnlyList<GuideSection> Sections,
    IReadOnlyList<GuideHint> Hints)
{
    public IReadOnlyList<string> HeldCaps => ShortcutStep.ModifierCaps(Held);

    public bool IsEmpty => Sections.Count == 0;

    public int Count => Sections.Sum(s => s.Items.Count);
}

/// <summary>Picks the shortcuts that start with the held modifiers: the app's own first, then Most apps, then Windows.</summary>
public static class GuideContent
{
    private static readonly HotkeyModifiers[] s_modifiers =
        [HotkeyModifiers.Ctrl, HotkeyModifiers.Shift, HotkeyModifiers.Alt, HotkeyModifiers.Win];

    /// <param name="processName">The process of the window in front ("Code.exe").</param>
    /// <param name="includeGeneral">Also list Most apps and Windows.</param>
    public static GuideView Build(ShortcutLibrary library, string? processName, HotkeyModifiers held, bool includeGeneral)
    {
        var app = library.FindByProcess(processName);
        var sources = new List<GuideApp>(3);
        if (app is not null) sources.Add(app);
        if (includeGeneral)
        {
            sources.Add(library.Common);
            sources.Add(library.Windows);
        }

        var sections = new List<GuideSection>();
        // A shortcut listed by the app wins over the same keys in Most apps or Windows.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var items = new List<GuideItem>();
            foreach (var shortcut in source.Visible)
            {
                if (shortcut.Keys.LeadModifiers != held || !shortcut.Keys.HasLeadKey) continue;
                if (!taken.Add(shortcut.Keys.Canonical)) continue;
                items.Add(new GuideItem(shortcut.Keys.RemainingCaps(), shortcut.Does));
            }
            if (items.Count > 0) sections.Add(new GuideSection(source.Name, items));
        }

        var hints = new List<GuideHint>();
        if (held != HotkeyModifiers.None)
        {
            foreach (var extra in s_modifiers)
            {
                if (held.HasFlag(extra)) continue;
                var wanted = held | extra;
                var count = sources.SelectMany(s => s.Visible)
                    .Where(s => s.Keys.LeadModifiers == wanted && s.Keys.HasLeadKey)
                    .Select(s => s.Keys.Canonical)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (count > 0) hints.Add(new GuideHint(extra, count));
            }
        }

        var name = app?.Name ?? FriendlyProcessName(processName);
        return new GuideView(name, app is not null, held, sections, hints);
    }

    private static string FriendlyProcessName(string? processName) =>
        string.IsNullOrWhiteSpace(processName) ? "This app" : ShortcutLibrary.NormalizeProcess(processName);
}
