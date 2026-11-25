using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Helm.Ui.Windows;

/// <summary>
/// SPIKE diagnostics: what an Avalonia window receives from the keyboard and the input method (keys, characters, IME
/// composition and its strings). On only when HELM_INPUT_LOG names a file. Linked into Helm.Gallery too.
/// </summary>
public static class InputLog
{
    private static readonly string? File = Environment.GetEnvironmentVariable("HELM_INPUT_LOG");

    public static bool On => File is not null;

    public static void Write(string line)
    {
        if (File is null) return;
        try { System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch { /* diagnostics only */ }
    }

    /// <summary>Starts logging the messages of <paramref name="topLevel"/>'s window (once per window).</summary>
    public static void Attach(TopLevel topLevel, string who)
    {
        if (File is null) return;
        Avalonia.Controls.Win32Properties.AddWndProcHookCallback(topLevel, (IntPtr hwnd, uint msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            switch (msg)
            {
                case 0x0100: Write($"{who} WM_KEYDOWN vk=0x{(int)w:X2}"); break;
                case 0x0102: Write($"{who} WM_CHAR '{(char)(int)w}' 0x{(int)w:X4}"); break;
                case 0x010D: Write($"{who} WM_IME_STARTCOMPOSITION"); break;
                case 0x010E: Write($"{who} WM_IME_ENDCOMPOSITION"); break;
                case 0x010F:
                    Write($"{who} WM_IME_COMPOSITION flags=0x{(long)l:X} comp='{Read(hwnd, 0x8)}' result='{Read(hwnd, 0x800)}'");
                    // After Avalonia handled it: did the focused text box get the in-progress text?
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var focused = topLevel.FocusManager?.GetFocusedElement();
                        var presenter = (focused as Avalonia.Visual)?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault();
                        Write($"{who}   focused={focused?.GetType().Name ?? "none"} preedit='{presenter?.PreeditText}' text='{(focused as TextBox)?.Text}'");
                    });
                    break;
                case 0x0286: Write($"{who} WM_IME_CHAR '{(char)(int)w}'"); break;
                case 0x0281: Write($"{who} WM_IME_SETCONTEXT active={(int)w}"); break;
            }
            return IntPtr.Zero;
        });
        Write($"{who} attached");
    }

    private static string Read(IntPtr hwnd, int kind)
    {
        var context = ImmGetContext(hwnd);
        if (context == IntPtr.Zero) return "<no context>";
        try
        {
            var bytes = ImmGetCompositionStringW(context, kind, null, 0);
            if (bytes <= 0) return "";
            var buffer = new char[bytes / 2];
            ImmGetCompositionStringW(context, kind, buffer, bytes);
            return new string(buffer);
        }
        finally
        {
            ImmReleaseContext(hwnd, context);
        }
    }

    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(IntPtr context, int index, char[]? buffer, int length);
}
