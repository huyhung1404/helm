using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Helm.Core.Modules;
using Helm.Shell.Services;
using Microsoft.Win32;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). View glue
/// only: file dialogs, drag and drop, keys, scrolling (to a paragraph, and smoothly after the sentence being read
/// aloud, until the reader scrolls away by hand), and telling the view model which paragraph is at the top. While the
/// tool is off the library stays visible but read-only.
/// </summary>
public partial class NovelReaderContentPage : Page
{
    /// <summary>How long a smooth scroll takes.</summary>
    private static readonly TimeSpan ScrollTime = TimeSpan.FromMilliseconds(250);

    private readonly NovelReaderModule _module;
    private readonly NovelReaderViewModel _viewModel;
    private readonly IClipboardService _clipboard;
    private readonly Grammar.WordClassDictionary _wordClasses;
    private readonly DispatcherTimer _scrollTimer;
    private bool _scrolling;
    private bool _choosingChapter;
    private double _scrollFrom;
    private double _scrollTo;
    private DateTime _scrollStarted;

    public NovelScraperViewModel Scraper { get; }

    public NovelReaderContentPage(NovelReaderModule module, NovelReaderViewModel viewModel, IClipboardService clipboard,
        Grammar.WordClassDictionary wordClasses, NovelScraperViewModel scraper)
    {
        _module = module;
        _viewModel = viewModel;
        _clipboard = clipboard;
        _wordClasses = wordClasses;
        Scraper = scraper;
        SegmentText.WordClasses = wordClasses;
        DataContext = viewModel;
        InitializeComponent();
        _scrollTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        _scrollTimer.Tick += (_, _) => ScrollStep();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.ScrollRequested += (_, index) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => JumpTo(index));
        viewModel.FollowRequested += (_, index) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Follow(index));
        viewModel.Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WordEditorViewModel.IsOpen) && viewModel.Editor.IsOpen)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { DraftBox.Focus(); DraftBox.SelectAll(); });
        };
        if (viewModel.Reports is { } reports)
            reports.Editor.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Reports.ReportEditorViewModel.IsOpen) && reports.Editor.IsOpen)
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { CorrectBox.Focus(); CorrectBox.SelectAll(); });
            };
        Loaded += async (_, _) =>
        {
            LoadWordClasses();
            await viewModel.ActivateAsync();
            // Back on the page while listening: straight to the sentence being read.
            if (viewModel.IsSpeaking) viewModel.FollowReadingCommand.Execute(null);
        };
        Unloaded += (_, _) => viewModel.Deactivate();
        PreviewKeyDown += OnPreviewKeyDown;
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        ApplyEnabled();
    }

    // ---- Library ---------------------------------------------------------------------------------------------------

    private async void AddNovel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Add a novel", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await _viewModel.PrepareAddAsync(dialog.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _module.IsEnabled && DroppedFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!_module.IsEnabled || DroppedFile(e) is not { } path) return;
        e.Handled = true;
        if (_viewModel.IsBookOpen) _viewModel.ShowLibraryCommand.Execute(null);
        await _viewModel.PrepareAddAsync(path);
    }

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            && files[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? files[0] : null;

    // ---- Covers --------------------------------------------------------------------------------------------------

    private void Cover_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookCardViewModel card) _viewModel.OpenBookCommand.Execute(card);
    }

    private async void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BookCardViewModel card) return;
        var dialog = new OpenFileDialog
        {
            Title = "Cover picture for " + card.Title,
            Filter = "Pictures|" + string.Join(";", CoverPicture.Extensions.Select(x => "*" + x)),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await SetCoverAsync(card, dialog.FileName);
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        if (DroppedPicture(e) is null) return;
        e.Effects = _module.IsEnabled ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Card_Drop(object sender, DragEventArgs e)
    {
        if (!_module.IsEnabled || DroppedPicture(e) is not { } path || (sender as FrameworkElement)?.DataContext is not BookCardViewModel card) return;
        e.Handled = true;
        await SetCoverAsync(card, path);
    }

    private static string? DroppedPicture(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            && CoverPicture.IsPicture(files[0]) ? files[0] : null;

    /// <summary>Makes the picture small here (WPF decodes it) and hands the JPEG to the view model.</summary>
    private async Task SetCoverAsync(BookCardViewModel card, string path)
    {
        byte[] picture;
        try
        {
            // WPF imaging (RenderTargetBitmap) needs the UI thread; one picture is quick.
            picture = CoverPicture.Shrink(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _viewModel.Message = "That picture could not be read: " + ex.Message;
            return;
        }
        await _viewModel.SetCoverAsync(card, picture);
    }

    // ---- Reader ----------------------------------------------------------------------------------------------------

    private async void Chapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_choosingChapter || ChapterBox.SelectedIndex < 0) return;
        await _viewModel.GoToChapterAsync(ChapterBox.SelectedIndex);
    }

    private void Word_Clicked(object? sender, WordClickedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ParagraphViewModel paragraph }) _viewModel.OpenWord(paragraph, e.Segment);
    }

    /// <summary>The mouse held down on a word in study mode says its Chinese.</summary>
    private async void Word_Held(object? sender, WordHeldEventArgs e)
    {
        _viewModel.HeldWord.Show(e.Chinese, e.Sentence);
        await _viewModel.PronounceAsync(e.Chinese);
    }

    /// <summary>A double-click on a sentence reads aloud from it.</summary>
    private void Paragraph_ReadFromClicked(object? sender, WordClickedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ParagraphViewModel paragraph }) return;
        _viewModel.Editor.IsOpen = false;
        _viewModel.ReadFromSegment(paragraph, e.Index);
    }

    private void DraftBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var editor = _viewModel.Editor;
        if (Keyboard.Modifiers == ModifierKeys.Control) editor.SavePhraseCommand.Execute(null);
        else editor.SaveNameCommand.Execute(null);
        e.Handled = true;
    }

    private void ReadFromHere_Click(object sender, RoutedEventArgs e)
    {
        if (ParagraphOf(sender) is { } paragraph) _viewModel.ReadFrom(paragraph);
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (ParagraphOf(sender) is { } paragraph) _clipboard.SetText(paragraph.Text);
    }

    private void CopyChinese_Click(object sender, RoutedEventArgs e)
    {
        if (ParagraphOf(sender) is { } paragraph) _clipboard.SetText(paragraph.Chinese);
    }

    private static ParagraphViewModel? ParagraphOf(object sender) => (sender as FrameworkElement)?.DataContext as ParagraphViewModel;

    // ---- Reports of wrong translations -----------------------------------------------------------------------------

    /// <summary>A report from the paragraph's menu starts on its first sentence; the form widens it.</summary>
    private void ReportParagraph_Click(object sender, RoutedEventArgs e)
    {
        if (ParagraphOf(sender)?.Line is { } line) _viewModel.Reports?.Start(line, 0, 0);
    }

    private void ExportReports_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Reports is not { } reports) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save the reports of wrong translations",
            FileName = $"Translation reports {DateTime.Now:yyyy-MM-dd}.md",
            Filter = "Markdown (*.md)|*.md",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) reports.ExportTo(dialog.FileName);
    }

    /// <summary>Ctrl+Enter saves the report, Esc closes the form.</summary>
    private void ReportForm_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.Reports?.Editor is not { } editor) return;
        if (e.Key == Key.Escape)
        {
            editor.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            editor.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ExportBookNames_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenBookId is not { } id) return;
        var dialog = new SaveFileDialog { Title = "Export this novel's names", FileName = "Names.txt", Filter = "Text (*.txt)|*.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _viewModel.ExportEntriesTo(id, EntryKind.Name, dialog.FileName);
    }

    private void ImportBookNames_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenBookId is not { } id) return;
        var dialog = new OpenFileDialog { Title = "Import names into this novel", Filter = "Text (*.txt)|*.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _viewModel.ImportEntries(id, EntryKind.Name, dialog.FileName);
    }

    // ---- Player bar ------------------------------------------------------------------------------------------------

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e) => _viewModel.SeekTo((int)Math.Round(SeekBar.Value));

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _viewModel.SeekTo((int)Math.Round(SeekBar.Value)));

    private void CurrentSentence_Click(object sender, MouseButtonEventArgs e) => _viewModel.FollowReadingCommand.Execute(null);

    /// <summary>
    /// Space plays or pauses, Alt+←/→ skip a sentence and Alt+↑/↓ a paragraph, ← → change chapter, [ ] change the
    /// speed; not while typing. Scrolling keys stop following the voice.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsBookOpen || Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem || _viewModel.Editor.IsOpen
            || _viewModel.Reports?.Editor.IsOpen == true) return;
        var alt = e.Key == Key.System;
        switch (alt ? e.SystemKey : e.Key)
        {
            case Key.Space when !alt:
                _viewModel.PlayPauseCommand.Execute(null);
                break;
            case Key.Left when alt:
                _viewModel.PreviousSentenceCommand.Execute(null);
                break;
            case Key.Right when alt:
                _viewModel.NextSentenceCommand.Execute(null);
                break;
            case Key.Up when alt:
                _viewModel.PreviousParagraphCommand.Execute(null);
                break;
            case Key.Down when alt:
                _viewModel.NextParagraphCommand.Execute(null);
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.None:
                _viewModel.PreviousChapterCommand.Execute(null);
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.None:
                _viewModel.NextChapterCommand.Execute(null);
                break;
            case Key.OemPlus or Key.Add when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.ZoomInCommand.Execute(null);
                break;
            case Key.OemMinus or Key.Subtract when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.ZoomOutCommand.Execute(null);
                break;
            case Key.D0 or Key.NumPad0 when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.ResetZoomCommand.Execute(null);
                break;
            case Key.OemOpenBrackets:
                _viewModel.SlowerCommand.Execute(null);
                break;
            case Key.OemCloseBrackets:
                _viewModel.FasterCommand.Execute(null);
                break;
            case Key.PageUp or Key.PageDown or Key.Up or Key.Down or Key.Home or Key.End when !alt:
                _viewModel.StopFollowing();
                return;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---- Scrolling -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The wheel or a touchpad: the reader looks elsewhere, so the page stops following the voice. With Ctrl held it
    /// makes the text larger or smaller instead.
    /// </summary>
    private void Scroller_UserScroll(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Delta > 0) _viewModel.ZoomInCommand.Execute(null);
            else if (e.Delta < 0) _viewModel.ZoomOutCommand.Execute(null);
            e.Handled = true;
            return;
        }
        _viewModel.StopFollowing();
    }

    /// <summary>Dragging or clicking the scroll bar also stops following.</summary>
    private void Scroller_MouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != Scroller; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ScrollBar)
            {
                _viewModel.StopFollowing();
                return;
            }
        }
    }

    /// <summary>Opening a chapter or seeking puts the paragraph at the top, at once.</summary>
    private void JumpTo(int index)
    {
        _scrollTimer.Stop();
        // The paragraph to keep at the top from now on (another chapter's paragraphs are not the last one's).
        _top = (Math.Max(0, index), 0);
        if (index <= 0)
        {
            SetOffset(0);
            return;
        }
        if (Container(index) is not { } container) return;
        SetOffset(Scroller.VerticalOffset + TopOf(container) - 8);
    }

    /// <summary>
    /// Keeps the paragraph being read about a third of the way down, scrolling smoothly; in a paragraph taller than the
    /// view it follows the sentence instead. Nothing moves while the sentence is already comfortably in view.
    /// </summary>
    private void Follow(int index)
    {
        if (!_viewModel.IsFollowing || Container(index) is not { } container) return;
        var top = TopOf(container);
        var height = container.ActualHeight;
        var view = Scroller.ViewportHeight;
        var target = top;
        if (height > view * 0.6)
        {
            // The text shown: the words, or in study mode the word blocks.
            (FrameworkElement Text, double? Top)? sentence = _viewModel.IsStudyMode
                ? Find<StudyText>(container) is { } study ? (study, study.HighlightTop()) : null
                : Find<SegmentText>(container) is { } text ? (text, text.HighlightTop()) : null;
            if (sentence is { Top: { } sentenceTop } found) target = top + found.Text.TranslatePoint(new Point(0, sentenceTop), container).Y;
        }
        var comfortable = target >= view * 0.1 && target <= view * 0.55 && (height <= view * 0.6 || target + 60 <= view);
        if (comfortable) return;
        SmoothScroll(Scroller.VerticalOffset + target - view / 3);
    }

    private void SmoothScroll(double offset)
    {
        _scrollFrom = Scroller.VerticalOffset;
        _scrollTo = Math.Clamp(offset, 0, Scroller.ScrollableHeight);
        _scrollStarted = DateTime.UtcNow;
        _scrolling = true;
        _scrollTimer.Start();
    }

    private void ScrollStep()
    {
        var t = Math.Min(1, (DateTime.UtcNow - _scrollStarted) / ScrollTime);
        var eased = 1 - Math.Pow(1 - t, 3);
        Scroller.ScrollToVerticalOffset(_scrollFrom + (_scrollTo - _scrollFrom) * eased);
        if (t < 1) return;
        _scrollTimer.Stop();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _scrolling = _scrollTimer.IsEnabled);
    }

    private void SetOffset(double offset)
    {
        _scrolling = true;
        Scroller.ScrollToVerticalOffset(Math.Max(0, offset));
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _scrolling = _scrollTimer.IsEnabled);
    }

    private FrameworkElement? Container(int index) => ParagraphList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private double TopOf(FrameworkElement container) => container.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>
    /// Tells the view model which paragraph is at the top, so the place reading stopped is saved and synced. When the
    /// text gets wider or narrower (the names panel, the window) every paragraph wraps again: the paragraph at the top
    /// is put back where it was instead of the page showing other text.
    /// </summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_rewrapping) return;
        // The text wrapped again without the reader scrolling: a new width (the names panel, the window), a new text
        // size or another view (study mode, the Chinese under each paragraph). Put the paragraph at the top back.
        var rewrapped = e.ViewportWidthChange != 0 || e.ExtentHeightChange != 0 && e.VerticalChange == 0 && !_scrolling;
        if (rewrapped && _top is { } top)
        {
            _rewrapping = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _rewrapping = false;
                if (Container(top.Paragraph) is { } container)
                    SetOffset(Scroller.VerticalOffset + TopOf(container) + top.Within * container.ActualHeight);
            });
            return;
        }
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        var count = ParagraphList.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (Container(i) is not { } container) continue;
            var at = TopOf(container);
            if (at + container.ActualHeight <= 8) continue;
            _top = (i, container.ActualHeight > 0 ? Math.Clamp(-at / container.ActualHeight, 0, 1) : 0);
            // Scrolling by itself (following the voice, opening a chapter) is not the reader moving.
            if (!_scrolling) _viewModel.ReportVisibleParagraph(i);
            return;
        }
    }

    /// <summary>The paragraph at the top of the view, and how far down it the view starts (0 to 1).</summary>
    private (int Paragraph, double Within)? _top;

    /// <summary>The text is wrapping again; the place is put back once it is laid out.</summary>
    private bool _rewrapping;

    /// <summary>The word classes load in the background while the grammar colours are on (once; about 0.15 s).</summary>
    private void LoadWordClasses()
    {
        if (_viewModel.HoverHighlight && _viewModel.GrammarColors) _ = _wordClasses.LoadAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NovelReaderViewModel.HoverHighlight) or nameof(NovelReaderViewModel.GrammarColors)) LoadWordClasses();
        if (e.PropertyName == nameof(NovelReaderViewModel.ChapterIndex))
        {
            _choosingChapter = true;
            ChapterBox.SelectedIndex = _viewModel.ChapterIndex;
            _choosingChapter = false;
        }
        // Another chapter in the preview starts at its top.
        if (e.PropertyName == nameof(NovelReaderViewModel.SelectedPreviewChapter)) PreviewScroll.ScrollToTop();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>Line height = text size × spacing.</summary>
public sealed class LineHeight : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double size, double spacing] && size > 0 && spacing > 0 ? size * spacing : double.NaN;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
