using Avalonia.Controls;
using Helm.Core.Settings;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>Where pictures come from on this platform (a file dialog on the PC; the gallery and the camera on a phone).</summary>
public interface IBugPictureSource
{
    /// <summary>Pictures the user picks, at most <paramref name="max"/> (PC: a file dialog; phone: the gallery).</summary>
    Task<IReadOnlyList<BugImageDraft>> PickAsync(int max);

    /// <summary>The phone's camera is there: "Take a photo" shows.</summary>
    bool CanTakePhoto => false;

    Task<BugImageDraft?> TakePhotoAsync() => Task.FromResult<BugImageDraft?>(null);

    /// <summary>Pictures on the clipboard (a copied picture or copied picture files); empty when there is none.</summary>
    Task<IReadOnlyList<BugImageDraft>> PasteAsync();

    /// <summary>Opening a picture full size in the system's viewer works here: the picture sheet offers it.</summary>
    bool CanOpenPicture => false;

    /// <summary>Opens the picture's bytes in the system's viewer (PC: a temp copy in the default app).</summary>
    Task OpenPictureAsync(byte[] bytes, string? name) => Task.CompletedTask;
}

/// <summary>What each platform does its own way around the shared Bug reports page.</summary>
public interface IBugReportsPageHost : IBugPictureSource
{
    /// <summary>Opens the report form (PC: the small window, as Ctrl+Alt+F12 does; phone: the report page).</summary>
    void Report();

    /// <summary>How to report from anywhere, for the button's tooltip and the empty list ("Ctrl+Alt+F12, anywhere").</summary>
    string ReportHint { get; }

    /// <summary>The phone's "Shake to report" (a setting of this device): the gear opens it. Null: no settings, no gear.</summary>
    ISettingsStore<BugReportSettings>? ShakeSettings => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>What the window or page around the report form does (the form itself is the same everywhere).</summary>
public interface IBugReportFormHost : IBugPictureSource
{
    /// <summary>The form is done with (Cancel, Close, Escape): close the window or leave the page. The form thaws the black box.</summary>
    void Close();

    /// <summary>"Open the bug": show it on the Bug reports page (the form closes too).</summary>
    void OpenBug(string id);

    /// <summary>A keyboard is there: the form shows "Ctrl+Enter to send · Esc to close" and Escape closes it.</summary>
    bool HasKeyboard => false;

    /// <summary>The form's header was pressed: a window without a title bar starts moving with the mouse (PC).</summary>
    void DragWindow() { }
}
