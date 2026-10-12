using Helm.Core.Hotkeys;

namespace Helm.Modules.ShortcutGuide;

/// <summary>One press of a shortcut: the modifiers held and the key pressed with them ("" for modifiers alone).</summary>
public sealed record ShortcutStep(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>Keycap labels in display order, e.g. ["Ctrl", "Shift", "P"].</summary>
    public IReadOnlyList<string> Caps
    {
        get
        {
            var caps = ModifierCaps(Modifiers);
            if (Key.Length > 0) caps.Add(Key);
            return caps;
        }
    }

    public override string ToString() => string.Join("+", Caps);

    internal static List<string> ModifierCaps(HotkeyModifiers modifiers)
    {
        var caps = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Win)) caps.Add("Win");
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) caps.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) caps.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) caps.Add("Shift");
        return caps;
    }
}

/// <summary>
/// A shortcut as written in the catalog: "Ctrl+Shift+P", "Win+Left", "Ctrl++" (the plus key) or a chord of several
/// presses separated by spaces, "Ctrl+K Ctrl+C". Key names are short and case-insensitive ("Left", "PgUp", "Del",
/// "Esc", "Plus"); arrows show as arrows.
/// </summary>
public sealed partial record ShortcutKeys(IReadOnlyList<ShortcutStep> Steps)
{
    private static readonly System.Text.RegularExpressions.Regex s_spacedPlus = SpacedPlus();

    private static readonly Dictionary<string, string> s_keyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = "←", ["Right"] = "→", ["Up"] = "↑", ["Down"] = "↓",
        ["PageUp"] = "PgUp", ["PgUp"] = "PgUp", ["PageDown"] = "PgDn", ["PgDn"] = "PgDn",
        ["Delete"] = "Del", ["Del"] = "Del", ["Escape"] = "Esc", ["Esc"] = "Esc", ["Insert"] = "Ins", ["Ins"] = "Ins",
        ["Return"] = "Enter", ["Enter"] = "Enter", ["Backspace"] = "Backspace", ["Tab"] = "Tab", ["Space"] = "Space",
        ["Home"] = "Home", ["End"] = "End", ["PrtSc"] = "PrtSc", ["PrintScreen"] = "PrtSc", ["Pause"] = "Pause",
        ["Plus"] = "+", ["Minus"] = "-", ["Menu"] = "Menu", ["Click"] = "Click", ["Wheel"] = "Wheel", ["Drag"] = "Drag",
    };

    /// <summary>The modifiers of the first press: what you hold to start the shortcut.</summary>
    public HotkeyModifiers LeadModifiers => Steps[0].Modifiers;

    /// <summary>
    /// True when the first press has a key besides its modifiers. Shortcuts made of modifiers alone ("Shift Shift")
    /// cannot be hinted while you hold a modifier.
    /// </summary>
    public bool HasLeadKey => Steps[0].Key.Length > 0;

    /// <summary>The same text in a canonical form ("ctrl + shift + p" → "Ctrl+Shift+P"): the key for hiding a shortcut.</summary>
    public string Canonical => string.Join(" ", Steps.Select(s => s.ToString()));

    /// <summary>
    /// What is left to press while the lead modifiers are already held: the first key, then any further presses.
    /// "Ctrl+K Ctrl+C" held at Ctrl gives [["K"], ["Ctrl", "C"]].
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> RemainingCaps()
    {
        var steps = new List<IReadOnlyList<string>>(Steps.Count) { new[] { Steps[0].Key } };
        for (var i = 1; i < Steps.Count; i++) steps.Add(Steps[i].Caps);
        return steps;
    }

    /// <summary>Every press as keycap labels.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AllCaps() => Steps.Select(s => s.Caps).ToList();

    public override string ToString() => Canonical;

    public static ShortcutKeys Parse(string text) =>
        TryParse(text, out var keys, out var error) ? keys : throw new FormatException(error);

    public static bool TryParse(string? text, out ShortcutKeys keys, out string error)
    {
        keys = null!;
        error = string.Empty;
        // "Ctrl + Shift + P" has spaces inside one press; spaces otherwise separate presses.
        var compact = s_spacedPlus.Replace(text ?? string.Empty, "+");
        var joined = compact.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (joined.Length == 0)
        {
            error = "Type the keys, e.g. Ctrl+Shift+P.";
            return false;
        }

        var steps = new List<ShortcutStep>(joined.Length);
        foreach (var press in joined)
        {
            if (!TryParseStep(press, out var step, out error)) return false;
            steps.Add(step);
        }
        keys = new ShortcutKeys(steps);
        return true;
    }

    private static bool TryParseStep(string press, out ShortcutStep step, out string error)
    {
        step = null!;
        error = string.Empty;
        var parts = new List<string>();
        var rest = press;
        // "Ctrl++" and "+" mean the plus key itself.
        if (rest == "+" || rest.EndsWith("++"))
        {
            parts.Add("+");
            rest = rest.Length > 1 ? rest[..^2] : string.Empty;
        }
        var modifierParts = rest.Length == 0 ? [] : rest.Split('+');
        if (modifierParts.Any(p => p.Length == 0))
        {
            error = $"“{press}” has an empty key.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = parts.Count > 0 ? "+" : null;
        foreach (var part in modifierParts)
        {
            var modifier = ParseModifier(part);
            if (modifier != HotkeyModifiers.None)
            {
                modifiers |= modifier;
                continue;
            }
            if (key is not null)
            {
                error = $"“{press}” has two keys ({key} and {part}); separate presses with a space.";
                return false;
            }
            key = NormalizeKey(part);
        }
        step = new ShortcutStep(modifiers, key ?? string.Empty);
        if (modifiers == HotkeyModifiers.None && step.Key.Length == 0)
        {
            error = $"“{press}” has no key.";
            return false;
        }
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\w)\s+\+\s+(?=\w)")]
    private static partial System.Text.RegularExpressions.Regex SpacedPlus();

    private static HotkeyModifiers ParseModifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" or "meta" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static string NormalizeKey(string key)
    {
        if (s_keyNames.TryGetValue(key, out var name)) return name;
        // Single characters and function keys in upper case: "p" → "P", "f5" → "F5".
        return key.Length == 1 || key.Length <= 3 && (key[0] is 'f' or 'F') && key[1..].All(char.IsDigit) ? key.ToUpperInvariant() : key;
    }
}
