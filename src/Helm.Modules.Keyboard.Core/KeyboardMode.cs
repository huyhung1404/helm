namespace Helm.Modules.Keyboard;

/// <summary>What the keyboard types: English and Vietnamese are Helm Keyboard with its V/E switch, Chinese is the
/// Chinese keyboard (Sogou or Microsoft Pinyin).</summary>
public enum KeyboardMode
{
    English,
    Vietnamese,
    Chinese,
}

public static class KeyboardModes
{
    /// <summary>Chinese (PRC); any Chinese keyboard counts as <see cref="KeyboardMode.Chinese"/>.</summary>
    public const int ChineseSimplified = 0x0804;

    /// <summary>The shortcut's round: English → Vietnamese → Chinese → English, Chinese left out when there is none.</summary>
    public static KeyboardMode Next(KeyboardMode mode, bool withChinese) => mode switch
    {
        KeyboardMode.English => KeyboardMode.Vietnamese,
        KeyboardMode.Vietnamese when withChinese => KeyboardMode.Chinese,
        _ => KeyboardMode.English,
    };

    /// <summary>A keyboard language counts as Chinese when its primary language is Chinese (0x04).</summary>
    public static bool IsChinese(int language) => (language & 0x3FF) == 0x04;

    /// <summary>The mode a window types in, from its keyboard's language and the V/E switch.</summary>
    public static KeyboardMode From(int language, bool vietnamese) =>
        IsChinese(language) ? KeyboardMode.Chinese : vietnamese ? KeyboardMode.Vietnamese : KeyboardMode.English;

    /// <summary>What the on-screen sign and the app list show.</summary>
    public static string Label(this KeyboardMode mode) => mode switch
    {
        KeyboardMode.Vietnamese => "VI",
        KeyboardMode.Chinese => "中",
        _ => "EN",
    };
}
