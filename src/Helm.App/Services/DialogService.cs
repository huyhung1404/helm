using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Helm.App.Services;

internal interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

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
