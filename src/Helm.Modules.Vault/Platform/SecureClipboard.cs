using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
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
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (clearAfter is not null)
        {
            // https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        }
        if (!TrySet(data)) return;

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
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _placed) Clipboard.Clear();
        }
        catch (COMException ex)
        {
            logger.LogDebug(ex, "Clipboard busy; could not clear it");
        }
        _placed = null;
    }

    private bool TrySet(DataObject data)
    {
        // Another program may hold the clipboard open for a moment.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(40);
            }
            catch (COMException ex)
            {
                logger.LogWarning(ex, "Could not copy to the clipboard");
            }
        }
        return false;
    }
}
