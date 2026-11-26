using System.Runtime.InteropServices;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Vault.Platform;

/// <summary>
/// Copies secrets the way password managers do on Windows: the copy is marked so that clipboard history (Win+V), the
/// cloud clipboard and clipboard monitors skip it, and it is cleared after a while if it is still what Vault put there.
/// UI thread only.
/// </summary>
internal sealed class SecureClipboard(ILogger<SecureClipboard> logger)
{
    private DispatcherTimer? _timer;
    private string? _placed;

    public void Copy(string text, TimeSpan? clearAfter)
    {
        // https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
        if (!WindowsClipboard.SetText(text, excludeFromHistory: clearAfter is not null))
        {
            logger.LogWarning("Could not copy to the clipboard");
            return;
        }

        _timer?.Stop();
        if (clearAfter is not { } delay || delay <= TimeSpan.Zero)
        {
            _placed = null;
            return;
        }
        _placed = text;
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) => ClearIfOurs();
        _timer.Start();
    }

    /// <summary>Clears the clipboard if it still holds the secret Vault copied (e.g. when the vault locks).</summary>
    public void ClearIfOurs()
    {
        _timer?.Stop();
        _timer = null;
        if (_placed is null) return;
        if (WindowsClipboard.ContainsText() && WindowsClipboard.GetText() == _placed && !Empty())
            logger.LogDebug("Clipboard busy; could not clear it");
        _placed = null;
    }

    /// <summary>Empties the clipboard (WindowsClipboard writes and reads only), retrying while another app holds it.</summary>
    private static bool Empty()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    return EmptyClipboard();
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(40);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();
}
