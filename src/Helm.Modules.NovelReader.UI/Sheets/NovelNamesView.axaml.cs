using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The open novel's names (see the XAML), beside the text or in a sheet.</summary>
public partial class NovelNamesView : UserControl
{
    private readonly NovelReaderViewModel? _viewModel;
    private readonly INovelReaderPageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelNamesView() => InitializeComponent();

    public NovelNamesView(NovelReaderViewModel viewModel, INovelReaderPageHost host, Action close)
    {
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
        FileButtons.IsVisible = host.CanUseTextFiles;
        CloseButton.Click += (_, _) => close();
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnChanged;
            viewModel.Entries.CollectionChanged += OnEntries;
            Update();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= OnChanged;
            viewModel.Entries.CollectionChanged -= OnEntries;
        };
    }

    /// <summary>Opens on the names found by a scan.</summary>
    public void ShowFound() => FoundTab.IsChecked = true;

    private void OnEntries(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Update();

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NovelReaderViewModel.EntryFilter)) Update();
    }

    private void Update() => NoNames.IsVisible = _viewModel is { Entries.Count: 0, EntryFilter.Length: 0 };

    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true }) return;
        var found = ReferenceEquals(sender, FoundTab);
        SavedPanel.IsVisible = !found;
        FoundPanel.IsVisible = found;
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.OpenBookId is not { } id || _host is null) return;
        if (await _host.PickSaveFileAsync("Export this novel's names", "Names.txt", ".txt") is { } path) _viewModel.ExportEntriesTo(id, EntryKind.Name, path);
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.OpenBookId is not { } id || _host is null) return;
        if (await _host.PickTextFileAsync("Import names into this novel") is { } path) _viewModel.ImportEntries(id, EntryKind.Name, path);
    }
}
