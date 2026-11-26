using Helm.BugReports.Ui;
using Helm.Shell.BugReports;

namespace Helm.Shell.Windows;

/// <summary>Pictures on this PC: a file dialog, the clipboard, and the default viewer for a picture full size.</summary>
internal class PcBugPictureSource : IBugPictureSource
{
    public Task<IReadOnlyList<BugImageDraft>> PickAsync(int max) => WindowsBugPictures.PickAsync(max);

    public Task<IReadOnlyList<BugImageDraft>> PasteAsync() => Task.FromResult(WindowsBugPictures.FromClipboard());

    public bool CanOpenPicture => true;

    public Task OpenPictureAsync(byte[] bytes, string? name) => WindowsBugPictures.OpenAsync(bytes);
}

/// <summary>The Bug reports page on the PC: the report window is the reporter's, the picture dialogs are this PC's.</summary>
internal sealed class PcBugReportsPageHost(IPcBugReporting reporting) : PcBugPictureSource, IBugReportsPageHost
{
    public void Report() => reporting.Report();

    public string ReportHint => reporting.ReportHint;
}
