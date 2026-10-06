namespace Helm.Modules.Ssh;

/// <summary>
/// Pasting into the terminal, the same on both platforms: text with a line break is confirmed first, because each
/// break runs a command and text copied from a web page can hide lines. Not needed when the shell asked for bracketed
/// paste (it then waits for Enter).
/// </summary>
public static class TerminalPaste
{
    /// <summary>True when pasting <paramref name="text"/> should be confirmed.</summary>
    public static bool NeedsConfirmation(string text, bool bracketed) => !bracketed && text.ReplaceLineEndings("\n").Contains('\n');

    /// <summary>What the confirmation says about <paramref name="text"/>.</summary>
    public static string Warning(string text)
    {
        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Count(c => c == '\n') + 1;
        return lines == 1
            ? "The text ends with a line break, so it runs as soon as it is pasted."
            : $"The text has {lines} lines. Each line break runs what comes before it, as if you pressed Enter.";
    }

    /// <summary>The text as the terminal sends it: line breaks become carriage returns (a terminal's Enter).</summary>
    public static string ForTerminal(string text) => text.ReplaceLineEndings("\r");
}
