using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>
/// The library part of <see cref="NovelReaderView"/> (see the XAML): adding a novel, its preview, the cards. A .txt file
/// dropped on the library is added; a picture dropped on a card becomes its cover (through the host).
/// </summary>
public partial class NovelLibraryView : UserControl
{
    private NovelReaderViewModel? _viewModel;
    private INovelReaderPageHost? _host;
    private bool _narrow;

    public NovelLibraryView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>"Edit" on a card: the novel's sheet (title, cover, web source, delete).</summary>
    public event EventHandler<BookCardViewModel>? EditRequested;

    /// <summary>Scraping from the web, on the PC; null on the phone.</summary>
    public INovelWebScraper? Scraper { get; private set; }

    /// <summary>The tip of a card's download button: the PC downloads, the phone asks the PC.</summary>
    public string NewChaptersTip { get; private set; } = "Get new chapters (on the PC)";

    public void Setup(NovelReaderViewModel viewModel, INovelReaderPageHost host)
    {
        _viewModel = viewModel;
        _host = host;
        Scraper = host.Scraper;
        NewChaptersTip = Scraper is null
            ? "Get new chapters: your PC downloads them and they sync here"
            : "Continue download: new chapters (and ones that failed) from the web, shown before they are saved";
        WebCard.DataContext = Scraper;
        WebCard.IsVisible = Scraper is not null;
        FileHint.Text = host.Scraper is null
            ? "A Chinese .txt file in any encoding. It is kept privately and syncs to your other devices."
            : "A Chinese .txt file in any encoding (or drop it here). It is kept privately and syncs to your other devices.";
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Books.CollectionChanged += (_, _) => Update();
        Update();
        Arrange();
    }

    /// <summary>A phone: one card per row, the chapters of a preview as a picker over the text.</summary>
    public void SetNarrow(bool narrow)
    {
        _narrow = narrow;
        FirstRunSteps.Columns = narrow ? 1 : 3;
        PreviewList.IsVisible = !narrow;
        PreviewPicker.IsVisible = narrow;
        Grid.SetColumn(PreviewText, narrow ? 0 : 1);
        Grid.SetRow(PreviewText, narrow ? 1 : 0);
        Grid.SetColumnSpan(PreviewText, narrow ? 2 : 1);
        PreviewText.Height = narrow ? 460 : 540;
        // The file card's button: an icon on a phone, beside the words on a wide screen.
        AddFileButton.Padding = narrow ? new Thickness(0) : new Thickness(14, 0);
        AddFileButton.Width = narrow ? 40 : double.NaN;
        Arrange();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Arrange();
    }

    /// <summary>Cards about 400 wide side by side; one per row on a phone.</summary>
    private void Arrange()
    {
        if (BookList.ItemsPanelRoot is UniformGrid grid)
            grid.Columns = _narrow ? 1 : Math.Max(1, Math.Min(3, (int)((Bounds.Width + 12) / 400)));
        // The preview's buttons fill their row: Retry only when chapters failed.
        var retry = _viewModel?.HasPreviewFailures == true && Scraper is not null;
        RetryButton.IsVisible = retry;
        PreviewButtons.ColumnDefinitions = ColumnDefinitions.Parse(retry ? "*,*,*" : "*,*");
        Grid.SetColumn(CancelAddButton, retry ? 2 : 1);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NovelReaderViewModel.HasBooks) or nameof(NovelReaderViewModel.IsAdding) or nameof(NovelReaderViewModel.NeedsDictionaries):
                Update();
                break;
            case nameof(NovelReaderViewModel.HasPreviewFailures):
                Arrange();
                break;
            // Another chapter in the preview starts at its top.
            case nameof(NovelReaderViewModel.SelectedPreviewChapter):
                PreviewScroll.Offset = default;
                break;
        }
    }

    private void Update()
    {
        if (_viewModel is not { } vm) return;
        FirstRun.IsVisible = vm.HasNoBooks && !vm.IsAdding;
        Heading.IsVisible = vm.HasBooks && !vm.IsAdding;
        Summary.Text = vm.Books.Count == 1
            ? "One novel. It syncs to your other devices, with where you stopped and the names you fixed."
            : string.Create(CultureInfo.InvariantCulture, $"{vm.Books.Count} novels. They sync to your other devices, with where you stopped and the names you fixed.");
        // First run: the hero card has the one button; the file card waits under it. Without dictionaries the first
        // step is getting them (a novel would show in Chinese).
        FileCard.IsVisible = vm.HasBooks;
        var dictionariesFirst = vm.NeedsDictionaries;
        FirstStepTitle.Text = dictionariesFirst ? "Get the dictionaries" : "Add a novel";
        FirstStepText.Text = dictionariesFirst
            ? "The QuickTranslator dictionaries, about 30 MB, once. They sync to your other devices."
            : Scraper is null
                ? "A Chinese .txt file in any encoding. It syncs to your other devices."
                : "A Chinese .txt file, or an address or a name scraped from the web.";
        FirstStepButton.Text = dictionariesFirst ? "Download the dictionaries" : "Add my first novel";
    }

    private async void AddFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _host is null) return;
        try
        {
            if (await _host.PickNovelAsync() is { } path) await _viewModel.PrepareAddAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.Message = "Could not read the file: " + ex.Message;
        }
    }

    /// <summary>The first run's one button: the dictionaries first, then a novel.</summary>
    private void FirstStep_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { NeedsDictionaries: true } vm)
        {
            vm.DownloadDictionariesCommand.Execute(null);
            return;
        }
        AddFile_Click(sender, e);
    }

    private void Retry_Click(object? sender, RoutedEventArgs e) => Scraper?.RetryFailedCommand.Execute(null);

    private void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BookCardViewModel card) EditRequested?.Invoke(this, card);
    }

    /// <summary>The PC downloads the new chapters from the web; the phone asks the PC to (it has no browser for it).</summary>
    private void NewChapters_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BookCardViewModel card || _viewModel is null) return;
        if (Scraper is { } scraper) scraper.ContinueCommand.Execute(card);
        else _viewModel.RequestUpdateCommand.Execute(card);
    }

    // ---- Dropping a novel on the library, a picture on a card ---------------------------------------------------------

    private bool CanDrop => IsEffectivelyEnabled && _viewModel is not null;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanDrop && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!CanDrop || _viewModel is null || _host is null || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;
        e.Handled = true;
        if (files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var card = (e.Source as Control)?.DataContext as BookCardViewModel;
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                await _viewModel.PrepareAddAsync(path);
                return;
            }
            if (card is not null && await _host.CoverFromFileAsync(path) is { } picture) await _viewModel.SetCoverAsync(card, picture);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _viewModel.Message = "Could not use what was dropped: " + ex.Message;
        }
    }

    /// <summary>The scroll back to the top of the preview's text happens after it is laid out.</summary>
    internal void ResetPreviewScroll() => Dispatcher.UIThread.Post(() => PreviewScroll.Offset = default, DispatcherPriority.Loaded);
}
