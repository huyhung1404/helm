using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>The black box sheet's body (<see cref="BlackBoxSheetModel"/>); <paramref name="close"/> closes the sheet.</summary>
public partial class BlackBoxView : UserControl
{
    private readonly Action? _close;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public BlackBoxView() => InitializeComponent();

    public BlackBoxView(BlackBoxSheetModel model, Action close)
    {
        _close = close;
        DataContext = model;
        InitializeComponent();
    }

    private void Filter_Changed(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BlackBoxSheetModel model || sender is not RadioButton { IsChecked: true, Tag: string tag }) return;
        if (Enum.TryParse<BlackBoxFilter>(tag, out var filter)) model.Filter = filter;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _close?.Invoke();
}
