namespace Helm.Modules.Ssh;

/// <summary>Keys a phone keyboard does not have, as a terminal sends them.</summary>
public static class TerminalKeys
{
    /// <summary>
    /// Ctrl plus a key, as a terminal sends it: Ctrl+C is 0x03, Ctrl+[ is Esc, Ctrl+? is Delete. Null for a key that has
    /// no control form (the key is then sent as it is).
    /// </summary>
    public static string? Ctrl(char key) => key switch
    {
        >= 'a' and <= 'z' => ((char)(key - 'a' + 1)).ToString(),
        >= 'A' and <= 'Z' => ((char)(key - 'A' + 1)).ToString(),
        '@' or ' ' or '2' => "\0",
        '[' or '3' => "\u001b",
        '\\' or '4' => "\u001c",
        ']' or '5' => "\u001d",
        '^' or '6' => "\u001e",
        '_' or '-' or '7' => "\u001f",
        '?' or '8' => "\u007f",
        _ => null,
    };
}
