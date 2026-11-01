namespace Helm.Typing;

/// <summary>What Helm and the Helm Keyboard input method both need to know about each other.</summary>
public static class KeyboardIds
{
    /// <summary>The input method's COM class.</summary>
    public static readonly Guid Clsid = new("6710956b-fb8a-44c4-bedf-ed7b2bc5961e");

    /// <summary>Its profile, under English (US).</summary>
    public static readonly Guid Profile = new("9294b09c-ed79-41b8-98b6-a3ddb5151a74");

    /// <summary>English (US), the language it is installed under.</summary>
    public const int EnglishUs = 0x0409;

    /// <summary>The input method as Windows' keyboard list writes it ("0409:{clsid}{profile}").</summary>
    public static string InputMethod => $"{EnglishUs:X4}:{{{Clsid.ToString().ToUpperInvariant()}}}{{{Profile.ToString().ToUpperInvariant()}}}";

    /// <summary>The V/E switch: a TSF global compartment, 1 = Vietnamese (VT_I4). Every app's input method follows it.</summary>
    public static readonly Guid VietnameseSwitch = new("7a8b8185-0051-4d0a-807c-b1f4ce5b7731");

    /// <summary>
    /// Helm's process id while it switches keyboards itself (EN / VI / 中, with its own shortcut), 0 when it stops: the
    /// input method then leaves its own Ctrl+Shift alone, as long as that process lives (Helm may end without saying).
    /// A TSF global compartment, so apps in a sandbox see it too.
    /// </summary>
    public static readonly Guid HelmSwitching = new("3f7d1c52-8a0e-4b6f-9d21-5c4e8b7a9f13");

    /// <summary>Typing options under HKEY_CURRENT_USER (DWORD values, 1 = on), read by the input method.</summary>
    public const string SettingsKey = @"Software\Helm\Keyboard";

    public const string ModernToneStyleValue = "ModernToneStyle";

    public const string SpellCheckValue = "SpellCheck";
}
