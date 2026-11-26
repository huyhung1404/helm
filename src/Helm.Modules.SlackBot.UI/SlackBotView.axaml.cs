using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.SlackBot.Ui;

/// <summary>What each app does its own way around the shared Slack Report page.</summary>
public interface ISlackBotPageHost
{
    /// <summary>The page came on screen, or the window came back to it.</summary>
    void Opened();

    /// <summary>Puts <paramref name="text"/> on the clipboard (the Slack app's manifest).</summary>
    void CopyText(string text);
}

/// <summary>
/// Slack Report itself (see the .axaml): the to-do list with what was handled today folded under it, and the
/// worth-knowing report under its tab. The reply is one sheet that follows <see cref="SlackBotInboxViewModel.ReplyTo"/>;
/// the settings are a sheet behind the gear (with the tool's on/off switch first). While Slack is not connected the
/// page is the first-run card; while the tool is off what it kept stays visible but read only.
/// </summary>
public partial class SlackBotView : UserControl
{
    private const double Narrow = 720;
    private readonly IModule? _module;
    private readonly SlackBotInboxViewModel? _inbox;
    private readonly SlackBotViewModel? _settings;
    private readonly ISlackBotPageHost? _host;
    private SheetHandle? _reply;
    private SheetHandle? _settingsSheet;
    private bool _handledOpen;
    private bool _knowing;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public SlackBotView() => InitializeComponent();

    public SlackBotView(IModule module, SlackBotInboxViewModel inbox, SlackBotViewModel settings, ISlackBotPageHost host)
    {
        _module = module;
        _inbox = inbox;
        _settings = settings;
        _host = host;
        DataContext = inbox;
        InitializeComponent();
    }

    /// <summary>The reply sheet, while it is open.</summary>
    public SheetHandle? ReplySheet => _reply;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        // Tabs and gear beside today's line on a wide page, on their own line under it on a phone (still on the right).
        Grid.SetColumn(Tabs, narrow ? 0 : 1);
        Grid.SetRow(Tabs, narrow ? 1 : 0);
        Grid.SetColumnSpan(Tabs, narrow ? 2 : 1);
        Tabs.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;
        Tabs.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        FirstRunSteps.Columns = narrow ? 1 : 3;
    }

    /// <summary>Each tab replaces the whole page under the tabs.</summary>
    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true }) return;
        // The other tab is still checked at this point: go by the one just picked.
        _knowing = ReferenceEquals(sender, KnowingTab);
        ApplyPanels();
        Scroller.Offset = default;
    }

    /// <summary>Opens the Worth knowing tab (as tapping it does).</summary>
    public void ShowKnowing() => KnowingTab.IsChecked = true;

    /// <summary>Unfolds what was handled today (as tapping its header does).</summary>
    public void ShowHandled()
    {
        if (!_handledOpen) ToggleHandled_Click(null, new RoutedEventArgs());
        Dispatcher.UIThread.Post(() => HandledList.BringIntoView(), DispatcherPriority.Background);
    }

    private void ToggleHandled_Click(object? sender, RoutedEventArgs e)
    {
        _handledOpen = !_handledOpen;
        Motion.SetTurn(HandledChevron, _handledOpen);
        HandledList.IsVisible = _handledOpen;
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings(openConnection: false);

    /// <summary>First run: the settings sheet open on the steps to connect Slack, the one thing to do.</summary>
    private void Connect_Click(object? sender, RoutedEventArgs e) => OpenSettings(openConnection: true);

    /// <summary>The gear: Slack Report's settings as a sheet over the page, not a page to navigate to.</summary>
    public SheetHandle? OpenSettings(bool openConnection)
    {
        if (_settings is null || _module is null || _host is null) return null;
        SheetHandle? sheet = null;
        var view = new SlackBotSettingsView(_settings, _module, _host, () => sheet?.Close(), openConnection);
        sheet = Sheet.Show(this, view, 560);
        _settingsSheet = sheet;
        return sheet;
    }

    // The module and view models outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _inbox is null || _settings is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _inbox.PropertyChanged += OnInboxChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        _inbox.Handled.CollectionChanged += OnHandledChanged;
        ApplyEnabled();
        ApplyConnection();
        UpdateLines();
        try
        {
            _host?.Opened();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Slack Report page check failed: {ex}");
        }
        Dispatcher.UIThread.Post(UpdateReply, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        if (_inbox is not null)
        {
            _inbox.PropertyChanged -= OnInboxChanged;
            _inbox.Handled.CollectionChanged -= OnHandledChanged;
        }
        if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
        CloseReply();
        // The sheet lives over the window, not in the page: it goes with the page.
        _settingsSheet?.Close();
        _settingsSheet = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnInboxChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SlackBotInboxViewModel.ReplyTo):
                Dispatcher.UIThread.Post(UpdateReply, DispatcherPriority.Loaded);
                break;
            case nameof(SlackBotInboxViewModel.ToDoCount) or nameof(SlackBotInboxViewModel.KnowingCount):
                UpdateLines();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlackBotViewModel.HasTokens)) Dispatcher.UIThread.Post(ApplyConnection);
    }

    private void OnHandledChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateLines();

    /// <summary>Today's line with the counts, and the folded header of what was handled.</summary>
    private void UpdateLines()
    {
        if (_inbox is null) return;
        var day = DateTime.Now.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        TodayLine.Text = _settings?.HasTokens == false
            ? day.ToUpperInvariant()
            : $"{day} · {_inbox.ToDoCount} to do · {_inbox.KnowingCount} worth knowing".ToUpperInvariant();
        var handled = _inbox.Handled.Count;
        HandledHeader.IsVisible = handled > 0;
        HandledTitle.Text = handled == 1 ? "Handled today · 1" : $"Handled today · {handled}";
    }

    /// <summary>Not connected yet: the first-run card instead of the lists (and no tabs to pick from).</summary>
    private void ApplyConnection()
    {
        var connected = _settings?.HasTokens != false;
        FirstRun.IsVisible = !connected;
        TabGroup.IsVisible = connected;
        StatusRow.IsVisible = connected;
        ApplyPanels();
        UpdateLines();
    }

    private void ApplyPanels()
    {
        var connected = _settings?.HasTokens != false;
        ToDoPanel.IsVisible = connected && !_knowing;
        KnowingPanel.IsVisible = connected && _knowing;
    }

    /// <summary>Opens the reply sheet while the view model has a message to reply to, closes it when it has none.</summary>
    private void UpdateReply()
    {
        if (_inbox is null) return;
        if (_inbox.ReplyTo is null || VisualRoot is null)
        {
            CloseReply();
            return;
        }
        if (_reply is { IsOpen: true }) return;
        var vm = _inbox;
        var handle = Sheet.Show(this, new SlackBotReplyView(vm), 520);
        if (handle is null) return;
        _reply = handle;
        // Closed by its Cancel, a tap outside, Escape or Back: the reply is dropped too.
        handle.Closed += () =>
        {
            if (ReferenceEquals(_reply, handle)) _reply = null;
            if (vm.ReplyTo is not null) vm.CancelReplyCommand.Execute(null);
        };
    }

    private void CloseReply()
    {
        var reply = _reply;
        _reply = null;
        reply?.Close();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        StatusRow.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
