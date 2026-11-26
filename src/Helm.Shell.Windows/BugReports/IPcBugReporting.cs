namespace Helm.Shell.Windows;

/// <summary>What the Bug reports page asks of the app's reporter (Helm.App implements it: the black box and the tabs are its own).</summary>
public interface IPcBugReporting
{
    /// <summary>Freezes the black box and opens the report window (Ctrl+Alt+F12 does the same).</summary>
    void Report();

    /// <summary>How to report from anywhere, for the button's tooltip ("Ctrl+Alt+F12, anywhere").</summary>
    string ReportHint { get; }
}
