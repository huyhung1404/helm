using Microsoft.Win32;

namespace Helm.Keyboard.Tip;

/// <summary>
/// The typing options Helm writes to HKCU\Software\Helm\Keyboard (DWORD values, 1 = on), read each time the input
/// method is switched on. Apps that may not read the registry (sandboxed Store apps) get the defaults.
/// </summary>
internal static class Settings
{
    public const string KeyPath = @"Software\Helm\Keyboard";

    public static TelexOptions Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return TelexOptions.Default;
            return new TelexOptions
            {
                ModernToneStyle = Read(key, nameof(TelexOptions.ModernToneStyle), TelexOptions.Default.ModernToneStyle),
                SpellCheck = Read(key, nameof(TelexOptions.SpellCheck), TelexOptions.Default.SpellCheck),
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
