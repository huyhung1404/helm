using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab.Ui;

/// <summary>
/// Vocabulary itself, the same view on Windows, Android, macOS and iOS: a glass page with the top bar and three tabs
/// (Learn, Words, Roots), each replacing the whole page under the bar. The view model drives the sheets
/// (<see cref="VocabSheets"/>); the gear opens the settings as a sheet. On a PC the keys answer the card (Space, 1, 2,
/// L, S, K), walk an opened word or family (←, →, Esc) and add words (Ctrl+N, Ctrl+Shift+N). While the tool is off the
/// words stay visible but read only.
/// </summary>
public partial class VocabView : UserControl
{
    private const double Narrow = 720;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    private readonly IModule? _module;
    private readonly VocabViewModel? _viewModel;
    private readonly IVocabPageHost? _host;
    private readonly VocabSheets? _sheets;
    private SheetHandle? _settings;
    private SheetHandle? _menu;
    private bool _firstRunDone;
    private bool _syncingTabs;
    private string? _shownWord;
    private TopLevel? _topLevel;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public VocabView() => InitializeComponent();

    public VocabView(IModule module, VocabViewModel viewModel, IVocabPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Deleting a word asks in the same glass sheet on every platform.
        viewModel.UseDialogs(new GlassDialogs(() => this));
        InitializeComponent();
        FoldSection.SetFolds(this, viewModel.Settings);
        WordsPanel.AddHintText = host.AddHint;
        _sheets = new VocabSheets(this, viewModel);
        AnswerBar.Shown += (_, _) => UpdateMargins();
        viewModel.List.Pages.Turned += (_, _) => ScrollToTop();
        viewModel.Roots.Pages.Turned += (_, _) => ScrollToTop();
    }

    /// <summary>The sheets that follow the view model (for tests).</summary>
    public VocabSheets? Sheets => _sheets;

    /// <summary>The settings sheet, while it is open.</summary>
    public SheetHandle? SettingsSheet => _settings is { IsOpen: true } ? _settings : null;

    private bool IsNarrow => Classes.Contains("narrow");

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        MenuRow.IsVisible = _host?.OpenMenu is not null;
        // Tabs and gear beside today's line on a wide page, on their own line under it on a phone, on the left (as on every page).
        Grid.SetColumn(Tabs, narrow ? 0 : 1);
        Grid.SetRow(Tabs, narrow ? 1 : 0);
        Grid.SetColumnSpan(Tabs, narrow ? 2 : 1);
        Tabs.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;
        Tabs.HorizontalAlignment = HorizontalAlignment.Left;
        FirstRunSteps.Columns = narrow ? 1 : 3;
        AnswerBar.Margin = narrow ? new Thickness(12, 0, 12, 12) : new Thickness(40, 0, 40, 18);
        LearnPanel.SetNarrow(narrow);
        WordsPanel.SetNarrow(narrow);
        UpdateMargins();
    }

    /// <summary>The page's inset, and room at its foot for the answer bar while it shows.</summary>
    private void UpdateMargins()
    {
        var narrow = IsNarrow;
        var bottom = AnswerBar.IsVisible ? (narrow ? 104 : 112) : narrow ? 28 : 40;
        Page.Margin = narrow ? new Thickness(16, 18, 16, bottom) : new Thickness(40, 28, 40, bottom);
    }

    // ---- tabs ------------------------------------------------------------------------------------------------------

    /// <summary>Each tab replaces the whole page under the top bar, from its title down.</summary>
    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _syncingTabs || sender is not RadioButton { IsChecked: true } tab) return;
        _viewModel.Tab = ReferenceEquals(tab, WordsTab) ? VocabTab.Words : ReferenceEquals(tab, RootsTab) ? VocabTab.Roots : VocabTab.Learn;
    }

    /// <summary>Opens a tab (as tapping it does).</summary>
    public void ShowTab(VocabTab tab)
    {
        if (_viewModel is not null) _viewModel.Tab = tab;
    }

    /// <summary>A word on its own page (the phone's pop-up card's "Open in Helm", a link from elsewhere).</summary>
    public void OpenWord(string id) => _viewModel?.Open(id);

    /// <summary>Shows what the tab and the opened word say; a new tab or another word starts at the top.</summary>
    private void ShowContent(bool animate)
    {
        if (_viewModel is not { } vm) return;
        var firstRun = !_firstRunDone && vm.List.Count == 0 && vm.PendingCount == 0 && vm.TextCount == 0;
        FirstRun.IsVisible = firstRun;
        TabGroup.IsVisible = !firstRun;
        AgentChip.IsVisible = !firstRun && (vm.IsAgentBusy || vm.HasPending || vm.TextCount > 0);
        LearnPanel.IsVisible = !firstRun && vm.IsLearning;
        WordsPanel.IsVisible = !firstRun && vm.IsWordsTab && !vm.HasDetail;
        WordPage.IsVisible = !firstRun && vm.IsWordsTab && vm.HasDetail;
        RootsPanel.IsVisible = !firstRun && vm.IsRootsTab;

        _syncingTabs = true;
        LearnTab.IsChecked = vm.IsLearning;
        WordsTab.IsChecked = vm.IsWordsTab;
        RootsTab.IsChecked = vm.IsRootsTab;
        _syncingTabs = false;

        if (!animate) return;
        Control? shown = LearnPanel.IsVisible ? LearnPanel : WordsPanel.IsVisible ? WordsPanel : WordPage.IsVisible ? WordPage : RootsPanel.IsVisible ? RootsPanel : null;
        if (shown is not null) Motion.Enter(shown, 0.97);
        ScrollToTop();
    }

    private void ScrollToTop() => Dispatcher.UIThread.Post(() => Scroller.Offset = default);

    /// <summary>"THURSDAY 26 NOVEMBER · 8 DUE NOW": the day and what it asks of you (each tab says the rest).</summary>
    private void UpdateTodayLine()
    {
        if (_viewModel is not { } vm) return;
        TodayLine.Text = (vm.Today.Total == 0 ? "" : vm.Today.Due > 0 ? $"{vm.Today.Due} due now" : "all caught up").ToUpper(English);
        TodayLine.IsVisible = TodayLine.Text.Length > 0;
    }

    // ---- sheets ----------------------------------------------------------------------------------------------------

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>The gear: Vocabulary's settings as a sheet over the page, not a page to navigate to.</summary>
    public SheetHandle? OpenSettings()
    {
        if (_viewModel is null || _module is null || _host is null) return null;
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new VocabSettingsView(_viewModel, _module, _host, () => sheet?.Close()), 520);
        return _settings = sheet;
    }

    /// <summary>The Learn card's "More" (⋯) menu.</summary>
    public SheetHandle? OpenCardMenu() => _menu = LearnPanel.OpenCardMenu();

    /// <summary>First run: the Words tab with the cursor in "Add a word", the one step to take.</summary>
    private void AddFirst_Click(object? sender, RoutedEventArgs e)
    {
        _firstRunDone = true;
        GoAdd();
    }

    /// <summary>"Add words" anywhere (Ctrl+N, the Learn tab's button, the phone's own ways in): the add box on the Words tab.</summary>
    private void GoAdd()
    {
        if (_viewModel is not { } vm) return;
        if (vm.HasDetail) vm.CloseDetailCommand.Execute(null);
        vm.Tab = VocabTab.Words;
        ShowContent(animate: true);
        Dispatcher.UIThread.Post(WordsPanel.FocusAdd, DispatcherPriority.Input);
    }

    /// <summary>
    /// Android's Back: a sheet first, then the part being edited, then a word's or a family's page, then root practice;
    /// false lets the shell go back.
    /// </summary>
    public bool HandleBack()
    {
        if (Sheet.CloseTopmost()) return true;
        if (_viewModel is not { } vm) return false;
        if (vm.Detail is { IsEditingPart: true } detail)
        {
            detail.CancelEditPartCommand.Execute(null);
            return true;
        }
        if (vm.IsWordsTab && vm.HasDetail)
        {
            vm.CloseDetailCommand.Execute(null);
            return true;
        }
        if (vm.IsRootsTab && vm.Roots.HasSelected)
        {
            vm.Roots.CloseSelectedCommand.Execute(null);
            return true;
        }
        if (vm.IsRootsTab && vm.Roots.HasPractice)
        {
            vm.Roots.ClosePracticeCommand.Execute(null);
            return true;
        }
        return false;
    }

    private bool IsSheetOpen => _settings is { IsOpen: true } || _menu is { IsOpen: true } || _sheets?.Open.Any(h => h is { IsOpen: true }) == true;

    // ---- keys (a PC) -----------------------------------------------------------------------------------------------

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm || _module?.IsEnabled == false || IsSheetOpen || !IsEffectivelyVisible) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.N)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.Texts.OpenCommand.Execute(null);
            else GoAdd();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.F && vm.IsWordsTab)
        {
            if (vm.HasDetail) vm.CloseDetailCommand.Execute(null);
            Dispatcher.UIThread.Post(WordsPanel.FocusSearch, DispatcherPriority.Input);
            e.Handled = true;
            return;
        }
        // Typing in a box: its keys are its own.
        if (e.KeyModifiers != KeyModifiers.None || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        var command = vm.Tab switch
        {
            VocabTab.Learn => LearnKey(vm.Card, e.Key),
            VocabTab.Words when vm.HasDetail => e.Key switch
            {
                Key.Escape => vm.CloseDetailCommand,
                Key.Left => vm.PreviousWordCommand,
                Key.Right => vm.NextWordCommand,
                _ => null,
            },
            VocabTab.Roots => RootsKey(vm.Roots, e.Key),
            _ => null,
        };
        if (command is null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    private static System.Windows.Input.ICommand? LearnKey(VocabCardViewModel card, Key key) => !card.HasWord ? null : key switch
    {
        Key.Space or Key.Enter when !card.IsRevealed => card.RevealCommand,
        Key.D1 or Key.NumPad1 when card.IsRevealed => card.ForgotCommand,
        Key.D2 or Key.NumPad2 when card.IsRevealed => card.RememberedCommand,
        Key.L => card.SayCommand,
        Key.S => card.SkipCommand,
        Key.K => card.KnowCommand,
        _ => null,
    };

    private static System.Windows.Input.ICommand? RootsKey(VocabRootsViewModel roots, Key key)
    {
        if (roots.HasSelected)
            return key switch
            {
                Key.Escape => roots.CloseSelectedCommand,
                Key.Left => roots.PreviousFamilyCommand,
                Key.Right => roots.NextFamilyCommand,
                _ => null,
            };
        if (!roots.HasPractice) return null;
        return key switch
        {
            Key.Space or Key.Enter => roots.IsPracticeRevealed ? roots.NextRootCommand : roots.RevealRootCommand,
            Key.Escape => roots.ClosePracticeCommand,
            _ => null,
        };
    }

    // ---- living on the page ----------------------------------------------------------------------------------------

    // The module and view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _viewModel is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Roots.PropertyChanged += OnRootsChanged;
        _sheets?.Attach();
        // The keys work wherever the focus is on the page (or nowhere yet): they are heard at the window.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        ApplyEnabled();
        UpdateTodayLine();
        ShowContent(animate: false);
        Shown();
        if (_viewModel.IsAddOpen) OnAddAsked();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Roots.PropertyChanged -= OnRootsChanged;
        }
        _sheets?.Detach();
        _topLevel?.RemoveHandler(KeyDownEvent, OnKey);
        _topLevel = null;
        _settings?.Close();
        _menu?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>On screen again (opened, or back at the app on it). The platform calls it when the window or the app comes back.</summary>
    public void Shown()
    {
        try
        {
            _host?.Opened();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Vocabulary page check failed: {ex}");
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VocabViewModel.Tab):
                ShowContent(animate: true);
                break;
            case nameof(VocabViewModel.IsAgentBusy):
                ShowContent(animate: false);
                break;
            case nameof(VocabViewModel.Detail):
                // The opened word is rebuilt after every change (a sync, an answer): only another word starts at the top.
                var id = _viewModel?.Detail?.Id;
                var other = id != _shownWord;
                _shownWord = id;
                ShowContent(animate: other && _viewModel?.IsWordsTab == true);
                break;
            case nameof(VocabViewModel.Today):
                UpdateTodayLine();
                break;
            case nameof(VocabViewModel.PendingCount) or nameof(VocabViewModel.TextCount) or nameof(VocabViewModel.IsAgentBusy):
                ShowContent(animate: false);
                break;
            case nameof(VocabViewModel.IsAddOpen) when _viewModel?.IsAddOpen == true:
                OnAddAsked();
                break;
        }
    }

    /// <summary>"Add words" from the view model (the phone's +, a shortcut): the add box, then the state is spent.</summary>
    private void OnAddAsked()
    {
        _viewModel?.CloseAddCommand.Execute(null);
        _firstRunDone = true;
        Dispatcher.UIThread.Post(GoAdd);
    }

    private void OnRootsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabRootsViewModel.Selected) or nameof(VocabRootsViewModel.Practice) && _viewModel?.IsRootsTab == true)
            ScrollToTop();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        AnswerBar.IsEnabled = _module.IsEnabled;
        AgentChip.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
