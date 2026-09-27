namespace Helm.Core.Services;

/// <summary>Windows notifications from Helm's tray icon, for modules that need the user's attention.</summary>
public interface IUserNotifications
{
    /// <summary>Shows a notification; <paramref name="onClick"/> runs on the UI thread if the user clicks it.</summary>
    void Show(string title, string message, Action? onClick = null);
}
