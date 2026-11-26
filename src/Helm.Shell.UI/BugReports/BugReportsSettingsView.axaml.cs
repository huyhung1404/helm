using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Core.Settings;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>The settings sheet of Bug reports: this device's "Shake to report" (only a phone has it).</summary>
public partial class BugReportsSettingsView : UserControl
{
    private readonly Action? _close;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public BugReportsSettingsView() => InitializeComponent();

    public BugReportsSettingsView(ISettingsStore<BugReportSettings> settings, Action close)
    {
        _close = close;
        InitializeComponent();
        ShakeSwitch.IsChecked = settings.Current.ShakeToReport;
        ShakeSwitch.IsCheckedChanged += (_, _) => settings.Update(s => s.ShakeToReport = ShakeSwitch.IsChecked == true);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _close?.Invoke();
}
