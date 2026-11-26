using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The chapters sheet (see the XAML). Picking one closes the sheet and opens it.</summary>
public partial class NovelChaptersSheet : UserControl
{
    private readonly NovelReaderViewModel? _viewModel;
    private readonly Action? _close;
    private bool _filling;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelChaptersSheet() => InitializeComponent();

    public NovelChaptersSheet(NovelReaderViewModel viewModel, Action close, double height)
    {
        _viewModel = viewModel;
        _close = close;
        DataContext = viewModel;
        InitializeComponent();
        // The list has a height of its own so it only builds the rows in view (a web novel has thousands).
        ChapterList.Height = Math.Clamp(height, 240, 520);
        CloseButton.Click += (_, _) => close();
        FilterBox.TextChanged += (_, _) => Fill();
        ChapterList.SelectionChanged += OnPicked;
        Fill();
    }

    private void Fill()
    {
        if (_viewModel is null) return;
        _filling = true;
        var filter = FilterBox.Text?.Trim() ?? "";
        var items = filter.Length == 0
            ? _viewModel.Chapters.ToList()
            : _viewModel.Chapters.Where(c => c.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                             || (c.Index + 1).ToString(CultureInfo.InvariantCulture) == filter).ToList();
        ChapterList.ItemsSource = items;
        var current = items.FirstOrDefault(c => c.Index == _viewModel.ChapterIndex);
        ChapterList.SelectedItem = current;
        _filling = false;
        if (current is not null) Dispatcher.UIThread.Post(() => ChapterList.ScrollIntoView(current), DispatcherPriority.Loaded);
    }

    private async void OnPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_filling || _viewModel is null || ChapterList.SelectedItem is not ChapterItem chapter || chapter.Index == _viewModel.ChapterIndex) return;
        _close?.Invoke();
        try
        {
            await _viewModel.GoToChapterAsync(chapter.Index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _viewModel.Message = "Could not open the chapter: " + ex.Message;
        }
    }
}
