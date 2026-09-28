namespace Helm.Shell.Services;

/// <summary>A yes/no question in the platform's own dialog style.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

public interface IClipboardService
{
    /// <summary>Best effort: a busy clipboard is ignored (the text stays selectable on screen).</summary>
    void SetText(string text);
}

public interface IDeviceInfo
{
    /// <summary>Suggested name for this device in the sync device list (computer name, phone model).</summary>
    string DeviceName { get; }
}
