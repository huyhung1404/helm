using Avalonia.Controls;

namespace Helm.Modules.Missions.Ui;

/// <summary>What each platform does its own way around the shared Missions page.</summary>
public interface IMissionsPageHost
{
    /// <summary>
    /// The page came on screen, or the app came back to it (Android asks for notifications once and catches today's
    /// reminder).
    /// </summary>
    void Opened();

    /// <summary>
    /// Hands every step as CSV to the user: a save dialog on desktop, the share sheet on Android. The host reports the
    /// result through <see cref="MissionsViewModel.ReportExport"/> or <see cref="MissionsViewModel.ReportExportFailure"/>.
    /// </summary>
    void ExportCsv(string csv);

    /// <summary>The export button's label ("Export CSV…" on desktop, "Share CSV" on a phone).</summary>
    string ExportLabel => "Export CSV…";

    /// <summary>Settings only this platform has (Android: the home-screen widget), as cards in the settings sheet. Null: none.</summary>
    Control? CreatePlatformSettings() => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}
