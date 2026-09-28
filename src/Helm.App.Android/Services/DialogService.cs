using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Shell.Services;

namespace Helm.App.Android.Services;

/// <summary>Yes/no questions shown as an overlay in MainView (Avalonia has no platform message box on Android).</summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    private TaskCompletionSource<bool>? _pending;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _confirmText = "OK";

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        _pending?.TrySetResult(false);
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsOpen = true;
        return _pending.Task;
    }

    [RelayCommand]
    private void Confirm() => Close(true);

    [RelayCommand]
    public void Cancel() => Close(false);

    private void Close(bool result)
    {
        IsOpen = false;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(result);
    }
}
