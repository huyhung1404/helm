using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Shell.Services;
using Helm.Core.Ui;
using Helm.Modules.NovelReader.Reports;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>
/// Novel Reader itself, the same view on Windows, Android, macOS and iOS (see the notes in the XAML): the library, and an
/// open novel with its text and player. The sheets live in NovelReaderView.Sheets.cs. Keys while a novel is open (not
/// while typing): Space plays or pauses, Alt+←/→ a sentence, Alt+↑/↓ a paragraph, ← → the chapter, [ ] the speed,
/// Ctrl+= / Ctrl+− / Ctrl+0 the text size. Reading aloud lives in the view model and goes on when the page is gone.
/// While the tool is off the library stays visible but read only.
/// </summary>
public partial class NovelReaderView : UserControl
{
    private const double Narrow = 720;

    private readonly IModule? _module;
    private readonly NovelReaderViewModel? _viewModel;
    private readonly INovelReaderPageHost? _host;
    private readonly IClipboardService? _clipboard;
    private bool _narrow;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelReaderView() => InitializeComponent();

    public NovelReaderView(IModule module, NovelReaderViewModel viewModel, INovelReaderPageHost host, IClipboardService clipboard)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        _clipboard = clipboard;
        DataContext = viewModel;
        // Every question (delete a novel, remove found names, remove the voice) in the same glass sheet on every platform.
        viewModel.UseDialogs(new GlassDialogs(() => this));
        SegmentText.WordClasses = host.WordClasses;
        InitializeComponent();
        Library.Setup(viewModel, host);
        Library.EditRequested += (_, card) => OpenBook(card);
        Text.SentenceHeld += (_, paragraph) => _heldParagraph = paragraph;
        Text.ParagraphMenu += (_, paragraph) => OpenParagraphMenu(paragraph);
        Player.OptionsRequested += (_, _) => OpenOptions();
        Text.ShowsPlayHere = host.HasPointer;
    }

    /// <summary>The last time the page came on screen: the library loaded and the novel read last opened.</summary>
    public Task Activation { get; private set; } = Task.CompletedTask;

    /// <summary>True below 720 px: the phone's layout.</summary>
    public bool IsNarrow => _narrow;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        var changed = narrow != _narrow;
        _narrow = narrow;
        Classes.Set("narrow", narrow);
        LibraryPage.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        Reader.Margin = narrow ? new Thickness(12, 12, 12, 10) : new Thickness(40, 20, 40, 18);
        MenuRow.IsVisible = _host?.OpenMenu is not null;
        Library.SetNarrow(narrow);
        Player.SetNarrow(narrow);
        Text.ShowsPlayHere = !narrow && _host?.HasPointer != false;
        // A phone keeps four buttons beside the title: the settings are one tap further, in the reading options.
        ReaderGear.IsVisible = !narrow;
        BookTitleText.FontSize = narrow ? 17 : 20;
        if (changed) UpdateSidePanel();
    }

    // The module and view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _viewModel is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Editor.PropertyChanged += OnEditorChanged;
        _viewModel.HeldSentence.PropertyChanged += OnHeldChanged;
        _viewModel.Books.CollectionChanged += OnBooksChanged;
        if (_viewModel.Reports is { } reports)
        {
            reports.PropertyChanged += OnReportsChanged;
            reports.Editor.PropertyChanged += OnReportEditorChanged;
        }
        ApplyEnabled();
        UpdateChapterTitle();
        UpdateLibraryLine();
        UpdateSidePanel();
        LoadWordClasses();
        Shown();
        Dispatcher.UIThread.Post(FollowViewModel, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Editor.PropertyChanged -= OnEditorChanged;
            _viewModel.HeldSentence.PropertyChanged -= OnHeldChanged;
            _viewModel.Books.CollectionChanged -= OnBooksChanged;
            if (_viewModel.Reports is { } reports)
            {
                reports.PropertyChanged -= OnReportsChanged;
                reports.Editor.PropertyChanged -= OnReportEditorChanged;
            }
            // Reading aloud goes on; only the place reading is at is saved.
            _viewModel.Deactivate();
        }
        CloseAllSheets();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// On screen again (opened, or back at the app on it): the library, the novel read last, and where reading is. The
    /// platform calls it when the window or the app comes back.
    /// </summary>
    public void Shown()
    {
        if (_viewModel is null) return;
        try
        {
            _host?.Opened();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Novel Reader page check failed: {ex}");
        }
        Activation = ActivateAsync();
    }

    private async Task ActivateAsync()
    {
        if (_viewModel is null) return;
        try
        {
            await _viewModel.ActivateAsync();
            Text.ShowPlace();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not awaited by anyone: never let it take Helm down.
            _viewModel.Message = "Could not open Novel Reader: " + ex.Message;
        }
    }

    /// <summary>
    /// The phone's Back: a sheet closes first, then an open novel goes back to the library (unless it is being read
    /// aloud: then Back leaves Novel Reader and it keeps reading). False: the platform goes back itself.
    /// </summary>
    public bool HandleBack()
    {
        if (Sheet.CloseTopmost()) return true;
        if (_viewModel is { IsBookOpen: true, IsSpeaking: false })
        {
            _viewModel.ShowLibraryCommand.Execute(null);
            return true;
        }
        return false;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NovelReaderViewModel.ChapterIndex):
                UpdateChapterTitle();
                break;
            case nameof(NovelReaderViewModel.IsBookOpen):
                if (_viewModel?.IsBookOpen == false) CloseReaderSheets();
                UpdateSidePanel();
                UpdateLibraryLine();
                break;
            case nameof(NovelReaderViewModel.ShowNamesPanel):
                UpdateSidePanel();
                break;
            case nameof(NovelReaderViewModel.HoverHighlight) or nameof(NovelReaderViewModel.GrammarColors):
                LoadWordClasses();
                break;
            case nameof(NovelReaderViewModel.FontSize) or nameof(NovelReaderViewModel.LineSpacing) or nameof(NovelReaderViewModel.Mode)
                or nameof(NovelReaderViewModel.ShowChinese):
                Text.Rewrapped();
                break;
            case nameof(NovelReaderViewModel.NeedsDictionaries):
                UpdateLibraryLine();
                break;
        }
    }

    private void OnReportsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslationReportsViewModel.IsPanelOpen)) UpdateSidePanel();
    }

    private void OnBooksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateLibraryLine();

    private void UpdateChapterTitle()
    {
        if (_viewModel is null) return;
        var index = _viewModel.ChapterIndex;
        ChapterTitle.Text = index >= 0 && index < _viewModel.Chapters.Count ? _viewModel.Chapters[index].Title : "";
    }

    /// <summary>The library's eyebrow: the tool, how many novels, and what reading aloud is doing.</summary>
    private void UpdateLibraryLine()
    {
        if (_viewModel is null) return;
        var count = _viewModel.Books.Count;
        var parts = new List<string> { "Novel Reader" };
        if (count > 0) parts.Add(count == 1 ? "1 novel" : count.ToString(CultureInfo.InvariantCulture) + " novels");
        if (_viewModel.NeedsDictionaries) parts.Add("no dictionaries yet");
        LibraryLine.Text = string.Join(" · ", parts).ToUpper(CultureInfo.CurrentCulture);
    }

    /// <summary>The word classes load in the background while the grammar colours are on (once; about 0.15 s).</summary>
    private void LoadWordClasses()
    {
        if (_viewModel is { HoverHighlight: true, GrammarColors: true } && _host?.WordClasses is { IsReady: false } classes) _ = classes.LoadAsync();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        var on = _module.IsEnabled;
        Library.IsEnabled = on;
        ReaderBody.IsEnabled = on;
        Player.IsEnabled = on;
        LibraryNotices.ShowOff(!on);
        ReaderNotices.ShowOff(!on);
    }

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    // ---- Keys ---------------------------------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _viewModel is not { IsBookOpen: true } vm || _module?.IsEnabled != true) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or ComboBox or ComboBoxItem or Slider) return;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var none = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Space when none:
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left when alt:
                vm.PreviousSentenceCommand.Execute(null);
                break;
            case Key.Right when alt:
                vm.NextSentenceCommand.Execute(null);
                break;
            case Key.Up when alt:
                vm.PreviousParagraphCommand.Execute(null);
                break;
            case Key.Down when alt:
                vm.NextParagraphCommand.Execute(null);
                break;
            case Key.Left when none:
                vm.PreviousChapterCommand.Execute(null);
                break;
            case Key.Right when none:
                vm.NextChapterCommand.Execute(null);
                break;
            case Key.OemPlus or Key.Add when ctrl:
                vm.ZoomInCommand.Execute(null);
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                vm.ZoomOutCommand.Execute(null);
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                vm.ResetZoomCommand.Execute(null);
                break;
            case Key.OemOpenBrackets when none:
                vm.SlowerCommand.Execute(null);
                break;
            case Key.OemCloseBrackets when none:
                vm.FasterCommand.Execute(null);
                break;
            case Key.PageUp or Key.PageDown or Key.Up or Key.Down or Key.Home or Key.End when !alt:
                // Scrolling keys: the reader looks elsewhere, the text stops following the voice.
                vm.StopFollowing();
                return;
            default:
                return;
        }
        e.Handled = true;
    }
}
