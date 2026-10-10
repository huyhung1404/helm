using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While a
/// novel is open the page is as tall as what is visible (like SSH's terminal), so the text has its own scrolling and
/// the player stays at the bottom. Reading aloud follows the sentence being read until the reader scrolls by hand.
/// The page is transient; reading aloud lives in the view model and goes on when the page is gone.
/// </summary>
public partial class NovelReaderContentPage : UserControl
{
    /// <summary>The shell's margins around a page (top and bottom).</summary>
    private const double PageMargins = 40;

    /// <summary>Scrolling within this long after the finger left the screen is still the reader's (a fling).</summary>
    private static readonly TimeSpan FlingTime = TimeSpan.FromSeconds(2);

    private enum Sheet
    {
        None,
        Word,
        Chapters,
        Options,
        Names,
    }

    private readonly NovelReaderModule _module;
    private readonly NovelReaderViewModel _viewModel;
    private readonly INovelPlatform _platform;
    private readonly IShellNavigation _navigation;
    private ScrollViewer? _pageScroller;
    private Sheet _sheet;
    private bool _touching;
    private DateTime _lastTouch = DateTime.MinValue;
    private DateTime _autoScrollUntil = DateTime.MinValue;
    private DispatcherTimer? _animation;
    private bool _pickingChapter;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public NovelReaderContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<NovelReaderModule>(), HelmAndroidServices.Current.GetRequiredService<NovelReaderViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<INovelPlatform>(), HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public NovelReaderContentPage(NovelReaderModule module, NovelReaderViewModel viewModel, INovelPlatform platform, IShellNavigation navigation)
    {
        _module = module;
        _viewModel = viewModel;
        _platform = platform;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        ParagraphList.AddHandler(SegmentText.WordTappedEvent, OnWordTapped);
        ParagraphList.AddHandler(SegmentText.WordDoubleTappedEvent, OnWordDoubleTapped);
        BookList.AddHandler(Button.ClickEvent, OnBookButton);
        Scroller.AddHandler(PointerPressedEvent, (_, _) => _touching = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(PointerReleasedEvent, (_, _) => EndTouch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(PointerCaptureLostEvent, (_, _) => EndTouch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.ScrollChanged += OnScrollChanged;
        // After the slider took the release (a tap on the bar moves it there first).
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(Seek), RoutingStrategies.Tunnel, handledEventsToo: true);
        ShowSheet(Sheet.None);
    }

    // Pages are transient and the module and view model are singletons: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _module.PageBack = HandleBack;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Editor.PropertyChanged += OnEditorChanged;
        _viewModel.ScrollRequested += OnScrollRequested;
        _viewModel.FollowRequested += OnFollowRequested;
        _pageScroller = this.FindAncestorOfType<ScrollViewer>();
        if (_pageScroller is not null) _pageScroller.PropertyChanged += OnPageScrollerChanged;
        _platform.StartPhoneVoices();
        ApplyEnabled();
        FitHeight();
        UpdateChapterTitle();
        // Back on the page: the sentence being read, or where reading stopped.
        if (_viewModel.IsSpeaking) OnFollowRequested(this, _viewModel.PlayerParagraph);
        else if (_viewModel.IsBookOpen) OnScrollRequested(this, _viewModel.PlayerParagraph);
        _ = ActivateAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        if (_module.PageBack == HandleBack) _module.PageBack = null;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Editor.PropertyChanged -= OnEditorChanged;
        _viewModel.ScrollRequested -= OnScrollRequested;
        _viewModel.FollowRequested -= OnFollowRequested;
        if (_pageScroller is not null) _pageScroller.PropertyChanged -= OnPageScrollerChanged;
        _pageScroller = null;
        _animation?.Stop();
        // Reading aloud goes on; only the place reading is at is saved.
        _viewModel.FlushProgress();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task ActivateAsync()
    {
        try
        {
            await _viewModel.ActivateAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not awaited by anyone: never let it take Helm down.
            _viewModel.Message = "Could not open Novel Reader: " + ex.Message;
        }
    }

    // ---- Back --------------------------------------------------------------------------------------------------------

    /// <summary>Android back: a sheet closes first, then a novel goes back to the library (unless it is being read aloud).</summary>
    private bool HandleBack()
    {
        if (_sheet != Sheet.None)
        {
            CloseSheet();
            return true;
        }
        if (_viewModel.IsBookOpen && !_viewModel.IsSpeaking)
        {
            _viewModel.ShowLibraryCommand.Execute(null);
            return true;
        }
        // Reading aloud: Back leaves Novel Reader and it keeps reading in the background.
        return false;
    }

    // ---- Height: the reader fills what is visible -----------------------------------------------------------------

    private void OnPageScrollerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty) FitHeight();
    }

    /// <summary>
    /// The shell puts pages in a scroller: the library grows with its cards and scrolls there, an open novel is as tall
    /// as the visible area (shorter while the keyboard is open) and scrolls its text itself.
    /// </summary>
    private void FitHeight()
    {
        if (_pageScroller is null) return;
        if (!_viewModel.IsBookOpen)
        {
            if (!double.IsNaN(Height)) Height = double.NaN;
            return;
        }
        var height = Math.Round(Math.Max(360, _pageScroller.Bounds.Height - PageMargins));
        if (double.IsNaN(Height) || Math.Abs(Height - height) > 1) Height = height;
        if (_pageScroller.Offset.Y > 0) _pageScroller.Offset = default;
        if (_sheet != Sheet.None) SheetBox.MaxHeight = height * 0.85;
    }

    // ---- The view model --------------------------------------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NovelReaderViewModel.IsBookOpen):
                FitHeight();
                if (!_viewModel.IsBookOpen) ShowSheet(Sheet.None);
                break;
            case nameof(NovelReaderViewModel.ChapterIndex):
                UpdateChapterTitle();
                break;
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WordEditorViewModel.IsOpen)) return;
        if (_viewModel.Editor.IsOpen) ShowSheet(Sheet.Word);
        else if (_sheet == Sheet.Word) ShowSheet(Sheet.None);
    }

    private void UpdateChapterTitle()
    {
        var index = _viewModel.ChapterIndex;
        ChapterTitle.Text = index >= 0 && index < _viewModel.Chapters.Count ? _viewModel.Chapters[index].Title : "";
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }

    // ---- Library ---------------------------------------------------------------------------------------------------

    private async void AddNovel_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await _platform.PickNovelAsync(CancellationToken.None) is { } path) await _viewModel.PrepareAddAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.Message = "Could not read the file: " + ex.Message;
        }
    }

    /// <summary>"Change cover" on a card (the other buttons of the cards use commands).</summary>
    private async void OnBookButton(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: "cover", DataContext: BookCardViewModel card }) return;
        e.Handled = true;
        try
        {
            if (await _platform.PickCoverAsync(CancellationToken.None) is { } picture) await _viewModel.SetCoverAsync(card, picture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.Message = "Could not set the cover: " + ex.Message;
        }
    }

    // ---- The text: words, scrolling, following the voice ------------------------------------------------------------

    private void OnWordTapped(object? sender, WordTappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is ParagraphViewModel paragraph) _viewModel.OpenWord(paragraph, e.Segment);
    }

    private void OnWordDoubleTapped(object? sender, WordTappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not ParagraphViewModel paragraph) return;
        _viewModel.Editor.CloseCommand.Execute(null);
        _viewModel.ReadFromSegment(paragraph, e.Index);
    }

    private void EndTouch()
    {
        _touching = false;
        _lastTouch = DateTime.UtcNow;
    }

    /// <summary>The reader scrolled (not the page following the voice): report the place, stop following.</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now < _autoScrollUntil || (!_touching && now - _lastTouch > FlingTime)) return;
        _animation?.Stop();
        _viewModel.StopFollowing();
        if (TopParagraph() is { } top) _viewModel.ReportVisibleParagraph(top);
    }

    /// <summary>The paragraph at the top of the view.</summary>
    private int? TopParagraph()
    {
        var offset = Scroller.Offset.Y;
        for (var i = 0; i < _viewModel.Paragraphs.Count; i++)
        {
            if (ParagraphList.ContainerFromIndex(i) is not Control container) continue;
            if (container.Bounds.Bottom > offset + 1) return i;
        }
        return null;
    }

    /// <summary>Where a paragraph (or the sentence being read in it) starts, from the top of the text.</summary>
    private double? TopOf(int index, bool sentence)
    {
        if (index < 0 || ParagraphList.ContainerFromIndex(index) is not Control container) return null;
        var top = container.Bounds.Top;
        if (sentence && container.GetVisualDescendants().OfType<SegmentText>().FirstOrDefault() is { } text && text.HighlightTop() is { } inner
            && text.TranslatePoint(new Point(0, inner), container) is { } point)
            top += point.Y;
        return top;
    }

    /// <summary>A chapter opened, the seek bar or a novel reopened: show the paragraph at the top at once.</summary>
    private void OnScrollRequested(object? sender, int index)
    {
        // The new paragraphs are laid out first.
        Dispatcher.UIThread.Post(() =>
        {
            if (TopOf(index, sentence: false) is not { } top)
            {
                if (index <= 0) ScrollTo(0, animate: false);
                return;
            }
            ScrollTo(top - 8, animate: false);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Reading aloud moved on: keep its sentence between a tenth and two thirds of the way down, scrolling smoothly.</summary>
    private void OnFollowRequested(object? sender, int index) => Dispatcher.UIThread.Post(() =>
    {
        if (TopOf(index, sentence: true) is not { } top) return;
        var view = Scroller.Viewport.Height;
        var offset = Scroller.Offset.Y;
        if (top >= offset + view * 0.1 && top <= offset + view * 0.66) return;
        ScrollTo(top - view * 0.25, animate: true);
    }, DispatcherPriority.Loaded);

    private void ScrollTo(double target, bool animate)
    {
        _animation?.Stop();
        target = Math.Clamp(target, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        var start = Scroller.Offset.Y;
        if (!animate || Math.Abs(target - start) > Scroller.Viewport.Height * 3)
        {
            _autoScrollUntil = DateTime.UtcNow.AddMilliseconds(300);
            Scroller.Offset = new Vector(0, target);
            return;
        }
        var began = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(280);
        _autoScrollUntil = began + duration + TimeSpan.FromMilliseconds(200);
        _animation = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - began) / duration);
            var eased = 1 - Math.Pow(1 - t, 3);
            Scroller.Offset = new Vector(0, start + (target - start) * eased);
            if (t >= 1) _animation?.Stop();
        });
        _animation.Start();
    }

    private void CurrentSentence_Tapped(object? sender, TappedEventArgs e) => _viewModel.FollowReadingCommand.Execute(null);

    private void Seek() => _viewModel.SeekTo((int)Math.Round(SeekBar.Value));

    // ---- Sheets ------------------------------------------------------------------------------------------------------

    private void ShowSheet(Sheet sheet)
    {
        _sheet = sheet;
        WordSheet.IsVisible = sheet == Sheet.Word;
        ChapterSheet.IsVisible = sheet == Sheet.Chapters;
        OptionsSheet.IsVisible = sheet == Sheet.Options;
        NamesSheet.IsVisible = sheet == Sheet.Names;
        SheetLayer.IsVisible = sheet != Sheet.None;
        if (sheet == Sheet.None) return;
        SheetBox.MaxHeight = Math.Max(240, Bounds.Height * 0.85);
        // Lists fill the sheet; the others are as tall as what they show.
        SheetBox.Height = sheet is Sheet.Chapters or Sheet.Names ? SheetBox.MaxHeight : double.NaN;
        if (sheet == Sheet.Chapters)
        {
            _pickingChapter = true;
            ChapterList.SelectedIndex = _viewModel.ChapterIndex;
            _pickingChapter = false;
            if (_viewModel.ChapterIndex >= 0) Dispatcher.UIThread.Post(() => ChapterList.ScrollIntoView(_viewModel.ChapterIndex), DispatcherPriority.Loaded);
        }
    }

    private void CloseSheet()
    {
        if (_sheet == Sheet.Word) _viewModel.Editor.CloseCommand.Execute(null);
        ShowSheet(Sheet.None);
    }

    private void Scrim_Tapped(object? sender, TappedEventArgs e) => CloseSheet();

    private void Chapters_Click(object? sender, RoutedEventArgs e) => ShowSheet(Sheet.Chapters);

    private void Options_Click(object? sender, RoutedEventArgs e) => ShowSheet(Sheet.Options);

    private void Names_Click(object? sender, RoutedEventArgs e)
    {
        // The names panel loads its list when shown.
        _viewModel.ShowNamesPanel = true;
        ShowSheet(Sheet.Names);
    }

    private async void ChapterList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pickingChapter || _sheet != Sheet.Chapters || ChapterList.SelectedIndex < 0) return;
        var index = ChapterList.SelectedIndex;
        ShowSheet(Sheet.None);
        try
        {
            await _viewModel.GoToChapterAsync(index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _viewModel.Message = "Could not open the chapter: " + ex.Message;
        }
    }

    private void SavedTab_Click(object? sender, RoutedEventArgs e) => ShowNamesTab(saved: true);

    private void SuggestionsTab_Click(object? sender, RoutedEventArgs e) => ShowNamesTab(saved: false);

    private void ShowNamesTab(bool saved)
    {
        SavedPanel.IsVisible = saved;
        SuggestionsPanel.IsVisible = !saved;
        SavedTab.Classes.Set("accent", saved);
        SuggestionsTab.Classes.Set("accent", !saved);
    }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        ShowSheet(Sheet.None);
        _navigation.ShowPage(typeof(NovelReaderPage));
    }
}
