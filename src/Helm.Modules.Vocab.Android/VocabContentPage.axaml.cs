using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). It lays out
/// the whole page area (<see cref="IFullPage"/>): its own app bar, the tabs, the scrolling content and the answer bar
/// at the bottom. While the tool is off the words stay visible but read-only, with a note pointing to the settings.
/// </summary>
public partial class VocabContentPage : UserControl, IFullPage
{
    private readonly VocabModule _module;
    private readonly VocabViewModel _viewModel;
    private TopLevel? _topLevel;
    private string? _shownWord;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public VocabContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<VocabModule>(), HelmAndroidServices.Current.GetRequiredService<VocabViewModel>())
    {
    }

    public VocabContentPage(VocabModule module, VocabViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Holding a part of a word's outline offers to make that part one of the words.
        TreeNodeHold.Attach(this);
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _module.PageBack = HandleBack;
        ShellIntents.ItemOffered += OnItemOffered;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Roots.PropertyChanged += OnRootsChanged;
        _viewModel.Card.PropertyChanged += OnCardChanged;
        _viewModel.List.Pages.Turned += OnPageTurned;
        _viewModel.Roots.Pages.Turned += OnPageTurned;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel?.InputPane is { } pane) pane.StateChanged += OnKeyboardChanged;
        ApplyEnabled();
        UpdateBars();
        OpenOfferedWord();
        // Back in Vocabulary: the pop-up's permissions may have changed in the phone's settings meanwhile.
        _module.PageShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        if (_module.PageBack == HandleBack) _module.PageBack = null;
        ShellIntents.ItemOffered -= OnItemOffered;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Roots.PropertyChanged -= OnRootsChanged;
        _viewModel.Card.PropertyChanged -= OnCardChanged;
        _viewModel.List.Pages.Turned -= OnPageTurned;
        _viewModel.Roots.Pages.Turned -= OnPageTurned;
        if (_topLevel?.InputPane is { } pane) pane.StateChanged -= OnKeyboardChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Android's back: a sheet first, then the part being edited, then a word's or a family's page, then root practice;
    /// false lets the shell go back.
    /// </summary>
    private bool HandleBack()
    {
        if (Sheets.HandleBack()) return true;
        if (_viewModel.Detail is { IsEditingPart: true } detail)
        {
            detail.CancelEditPartCommand.Execute(null);
            return true;
        }
        if (_viewModel.IsWordsTab && _viewModel.HasDetail)
        {
            _viewModel.CloseDetailCommand.Execute(null);
            return true;
        }
        if (_viewModel.IsRootsTab && _viewModel.Roots.HasSelected)
        {
            _viewModel.Roots.CloseSelectedCommand.Execute(null);
            return true;
        }
        if (_viewModel.IsRootsTab && _viewModel.Roots.HasPractice)
        {
            _viewModel.Roots.ClosePracticeCommand.Execute(null);
            return true;
        }
        return false;
    }

    private void Menu_Click(object? sender, RoutedEventArgs e) => ShellChrome.OpenNavigation(sender as Control ?? this);

    private void WordMenu_Click(object? sender, RoutedEventArgs e) => Sheets.Show(VocabSheet.WordMenu);

    // ---- Which app bar, and back to the top on a new page ------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VocabViewModel.Tab):
                UpdateBars();
                ScrollToTop();
                break;
            case nameof(VocabViewModel.Detail):
                UpdateBars();
                // The opened word is rebuilt after every change (a sync, an answer): only another word starts at the top.
                if (_viewModel.Detail?.Id != _shownWord)
                {
                    _shownWord = _viewModel.Detail?.Id;
                    ScrollToTop();
                }
                break;
        }
    }

    private void OnRootsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabRootsViewModel.Selected) or nameof(VocabRootsViewModel.Practice))
        {
            UpdateBars();
            if (_viewModel.IsRootsTab) ScrollToTop();
        }
    }

    // The next card starts at the top.
    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VocabCardViewModel.Text) && _viewModel.IsLearning) ScrollToTop();
    }

    private void OnPageTurned(object? sender, EventArgs e) => ScrollToTop();

    /// <summary>The tool's own app bar and tabs, or a word's or a family's (back, Previous, Next).</summary>
    private void UpdateBars()
    {
        var word = _viewModel.IsWordsTab && _viewModel.HasDetail;
        var family = _viewModel.IsRootsTab && _viewModel.Roots.HasSelected;
        HomeBar.IsVisible = Tabs.IsVisible = !word && !family;
        WordBar.IsVisible = word;
        FamilyBar.IsVisible = family;
    }

    private void ScrollToTop() => Dispatcher.UIThread.Post(() => MainScroll.Offset = default);

    // ---- The keyboard ------------------------------------------------------------------------------------------------

    /// <summary>The page gives the keyboard its room, so a sheet's box and the card's pinyin box stay above it.</summary>
    private void OnKeyboardChanged(object? sender, Avalonia.Controls.Platform.InputPaneStateEventArgs e)
    {
        if (_topLevel is null) return;
        var covered = e.EndRect.Height;
        var bottom = this.TranslatePoint(new Point(0, Bounds.Height), _topLevel) is { } p ? _topLevel.Bounds.Height - p.Y : 0;
        Root.Margin = new Thickness(0, 0, 0, Math.Max(0, covered - bottom));
    }

    // ---- Opened from outside, turned on and off ----------------------------------------------------------------------

    private void OnItemOffered(object? sender, EventArgs e) => Dispatcher.UIThread.Post(OpenOfferedWord);

    /// <summary>"Open in Helm" on the pop-up card: that word with its whole tree.</summary>
    private void OpenOfferedWord()
    {
        if (ShellIntents.TakeItem(VocabIds.ModuleId) is { } wordId) _viewModel.Open(wordId);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = Tabs.IsEnabled = AnswerBar.IsEnabled = AnalysisButton.IsEnabled = AddButton.IsEnabled = on;
        OffBar.IsVisible = !on;
    }
}
