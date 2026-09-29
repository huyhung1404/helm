using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.CommandPalette;

public enum WebSearchEngine
{
    Google,
    Bing,
    DuckDuckGo,
}

public sealed class CommandPaletteSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    /// <summary>Alt+Space, the usual launcher shortcut (it replaces the window menu that Alt+Space opens).</summary>
    public static readonly HotkeyGesture DefaultHotkey = new(HotkeyModifiers.Alt, 0x20);

    public static readonly IReadOnlyList<string> WebSearchNames = ["Google", "Bing", "DuckDuckGo"];

    public int Version { get; set; }

    public HotkeyGesture Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Also find the apps in the Start menu (and Microsoft Store apps) and open them.</summary>
    public bool IncludeApps { get; set; } = true;

    /// <summary>Files and folders from the Windows Search index.</summary>
    public bool IncludeFiles { get; set; } = true;

    /// <summary>The open windows (switch to one).</summary>
    public bool IncludeWindows { get; set; } = true;

    /// <summary>Pages of Windows Settings.</summary>
    public bool IncludeWindowsSettings { get; set; } = true;

    /// <summary>"Search the web for …" as the last result.</summary>
    public bool IncludeWebSearch { get; set; } = true;

    public WebSearchEngine WebSearch { get; set; } = WebSearchEngine.Google;

    /// <summary>The search page for <paramref name="text"/>; null for an unknown engine.</summary>
    public static string? WebSearchUrl(WebSearchEngine engine, string text)
    {
        var q = Uri.EscapeDataString(text);
        return engine switch
        {
            WebSearchEngine.Google => $"https://www.google.com/search?q={q}",
            WebSearchEngine.Bing => $"https://www.bing.com/search?q={q}",
            WebSearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={q}",
            _ => null,
        };
    }
}
