using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Modules.NovelReader.Reports;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The sentences marked as translated wrong (see the XAML), beside the text or in a sheet.</summary>
public partial class NovelReportsView : UserControl
{
    private readonly TranslationReportsViewModel? _reports;
    private readonly INovelReaderPageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelReportsView() => InitializeComponent();

    public NovelReportsView(TranslationReportsViewModel reports, INovelReaderPageHost host, Action close)
    {
        _reports = reports;
        _host = host;
        DataContext = reports;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
        // Saving a file: only where the platform can; Copy then fills the row.
        SaveButton.IsVisible = host.CanUseTextFiles;
        Grid.SetColumnSpan(DocButtons.Children[0], host.CanUseTextFiles ? 1 : 2);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_reports is null || _host is null) return;
        var name = $"Translation reports {DateTime.Now:yyyy-MM-dd}.md";
        if (await _host.PickSaveFileAsync("Save the reports of wrong translations", name, ".md") is { } path) _reports.ExportTo(path);
    }
}
