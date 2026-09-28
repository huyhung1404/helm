using Helm.Shell.Services;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Helm.App.Services;

internal sealed class DialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new WpfUiMessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            MinWidth = 380,
        };
        return await box.ShowDialogAsync().ConfigureAwait(true) == WpfUiMessageBoxResult.Primary;
    }
}

internal sealed class ClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard busy; the text is still selectable
    }
}

internal sealed class DeviceInfo : IDeviceInfo
{
    public string DeviceName => Environment.MachineName;
}
