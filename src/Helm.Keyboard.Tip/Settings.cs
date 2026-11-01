using Microsoft.Win32;

namespace Helm.Typing.Tip;

/// <summary>
/// The typing options Helm writes to HKCU\Software\Helm\Keyboard (DWORD values, 1 = on), read each time the input
/// method is switched on. Apps that may not read the registry (sandboxed Store apps) get the defaults.
/// </summary>
internal static class Settings
{
    public static TelexOptions Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyboardIds.SettingsKey);
            if (key is null) return TelexOptions.Default;
            return new TelexOptions
            {
                ModernToneStyle = Read(key, KeyboardIds.ModernToneStyleValue, TelexOptions.Default.ModernToneStyle),
                SpellCheck = Read(key, KeyboardIds.SpellCheckValue, TelexOptions.Default.SpellCheck),
            };
        }
        catch (Exception ex)
        {
            Log.Error("reading the settings", ex);
            return TelexOptions.Default;
        }
    }

    private static bool Read(RegistryKey key, string name, bool fallback) => key.GetValue(name) is int value ? value != 0 : fallback;
}
