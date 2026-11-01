using Helm.Typing.Tip.Interop;

namespace Helm.Typing.Tip;

/// <summary>
/// Messages for DebugView (OutputDebugString): the input method runs inside other apps, some of which may not write
/// files at all. While developing, HELM_KEYBOARD_LOG=&lt;file&gt; in an app's environment also appends them to that file.
/// </summary>
internal static class Log
{
    private static readonly string? File = Environment.GetEnvironmentVariable("HELM_KEYBOARD_LOG");

    public static bool IsVerbose => File is not null;

    public static void Info(string message) => Write($"[HelmKeyboard] {message}");

    public static void Error(string where, Exception ex) => Write($"[HelmKeyboard] {where} failed: {ex}");

    private static void Write(string line)
    {
        Native.OutputDebugString(line);
        if (File is null) return;
        try
        {
            System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // Only a development aid.
        }
    }
}
