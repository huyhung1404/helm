using System.ComponentModel;
using System.Windows;
using Helm.App.Services;
using Helm.App.ViewModels;
using Helm.App.Views.Pages;
using Helm.App.Views.Tabs;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Helm.App.Views;

internal partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IServiceProvider _services;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly NavPaneController _navPane;
    private readonly TabManager _tabs;

    public MainWindow(MainWindowViewModel viewModel, IServiceProvider services, ShellNavigator navigator, TabManager tabs, ISettingsStoreFactory settings)
    {
        _viewModel = viewModel;
        _tabs = tabs;
        _services = services;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        DataContext = viewModel;
        InitializeComponent();

        RestorePlacement();
        _navPane = new NavPaneController(Navigation, RootGrid, _general, TitleIcon);
        Navigation.SetServiceProvider(services);
        BuildNavigation();

        // The window's content is its tabs: the menu, search and Quick access open pages as tabs (one per page).
        tabs.AttachMain(services.GetRequiredService<TabsPage>().Host);
        tabs.MainActivePageChanged += (_, page) => HighlightMenu(page);
        tabs.RestoreMain(StartupPage());
        TabKeys.Attach(this, () => tabs.Main);

        // The sync button, and quicker pulls while the window is shown so other devices' changes show up within seconds.
        var sync = services.GetRequiredService<Helm.Shell.ViewModels.SyncIndicatorViewModel>();
        sync.OpenSettings = () => navigator.NavigateToCard(typeof(Pages.GeneralPage), "Sync");
        SyncButton.DataContext = sync;
        var engine = services.GetRequiredService<Core.Sync.SyncEngine>();
        IsVisibleChanged += (_, e) => engine.SetPollInterval(e.NewValue is true ? Core.Sync.SyncEngine.ForegroundPollInterval : Core.Sync.SyncEngine.BackgroundPollInterval);

        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.QuerySubmitted += (_, e) => NavigateTo(_viewModel.Search(e.QueryText).FirstOrDefault());
        SearchBox.SuggestionChosen += (_, e) => NavigateTo(e.SelectedItem as SearchResult);

        Loaded += (_, _) => Navigation.Navigate(typeof(TabsPage));
        // The search box gives way on a narrow window, so the sync button never slides under the window buttons.
        SizeChanged += (_, _) => FitSearchBox();
        Navigation.Navigated += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ConstrainPageWidth);
    }

    /// <summary>
    /// The title bar lays out the header (title, 16 + 292), the search box and the sync button from the left, and the
    /// window buttons (minimise, maximise, close: 3 × 46) on the right: the search box takes what is left, up to 520.
    /// </summary>
    private void FitSearchBox()
    {
        const double header = 16 + 292, windowButtons = 3 * 46, syncButton = 8 + 34, gap = 24;
        SearchBox.Width = Math.Clamp(ActualWidth - header - windowButtons - syncButton - gap, 160, 520);
    }

    /// <summary>
    /// WPF-UI hosts pages in a scroll viewer that also scrolls horizontally, so the tabs page would be measured with
    /// infinite width and the tab strip would never fit the window. Disabling horizontal scrolling on the host makes
    /// it fit the viewport (each tab does the same for its own page).
    /// </summary>
    private void ConstrainPageWidth()
    {
        foreach (var viewer in FindDescendants<System.Windows.Controls.ScrollViewer>(Navigation))
        {
            // Only the hosts between the NavigationView and the page, not scroll viewers inside page content.
            if (viewer.Content is System.Windows.Controls.Page or System.Windows.Controls.Frame
                || viewer.TemplatedParent is not null && FindAncestor<System.Windows.Controls.Page>(viewer) is null)
                viewer.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var p = System.Windows.Media.VisualTreeHelper.GetParent(node); p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is T match) return match;
        return null;
    }

    /// <summary>"--page Diagnostics" (dev/testing) opens that page's tab in front; without it the last session's tabs come back.</summary>
    private Type? StartupPage()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;
        var name = args[index + 1];
        var candidates = new[] { typeof(HomePage), typeof(GeneralPage), typeof(McpPage), typeof(DiagnosticsPage), typeof(WelcomePage), typeof(WhatsNewPage) }
            .Concat(_viewModel.Groups.SelectMany(g => g.Modules).Select(m => m.SettingsPageType))
            .Concat(_viewModel.Groups.SelectMany(g => g.Modules).OfType<IModuleContent>().Select(m => m.ContentPageType));
        return candidates.FirstOrDefault(t => t.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ?? typeof(HomePage);
    }

    /// <summary>Remembers size/position in general.json.</summary>
    public void SavePlacement()
    {
        // Before the window has ever been shown WPF reports Left/Top as NaN (CenterScreen) and RestoreBounds as
        // Rect.Empty (infinite); only persist real, finite placements.
        _tabs.SaveMain();
        if (!IsLoaded) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return;
        _general.Update(s =>
        {
            s.Window.Left = bounds.Left;
            s.Window.Top = bounds.Top;
            s.Window.Width = bounds.Width;
            s.Window.Height = bounds.Height;
            s.Window.IsMaximized = WindowState == WindowState.Maximized;
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (!_services.GetRequiredService<ShellController>().IsExiting)
        {
            // Closing the window only hides it; Helm keeps running in the tray.
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void RestorePlacement()
    {
        var p = _general.Current.Window;
        Width = double.IsFinite(p.Width) ? Math.Max(MinWidth, p.Width) : MinWidth;
        Height = double.IsFinite(p.Height) ? Math.Max(MinHeight, p.Height) : MinHeight;
        if (p.Left is { } left && p.Top is { } top && double.IsFinite(left) && double.IsFinite(top))
        {
            var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            // Only restore when the title bar would still be reachable on the current monitor setup.
            if (virtualScreen.Contains(new Point(left + 100, top + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        if (p.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void BuildNavigation()
    {
        Navigation.MenuItems.Clear();
        Navigation.FooterMenuItems.Clear();

        Navigation.MenuItems.Add(NavItem("Home", SymbolRegular.Home24, typeof(HomePage)));
        Navigation.MenuItems.Add(NavItem("General", SymbolRegular.Settings24, typeof(GeneralPage)));
        Navigation.MenuItems.Add(NavItem("AI & MCP", SymbolRegular.PlugConnected24, typeof(McpPage)));
        Navigation.MenuItems.Add(new NavigationViewItemSeparator());

        foreach (var group in _viewModel.Groups)
        {
            var groupItem = new NavigationViewItem
            {
                Content = group.Group.DisplayName(),
                Icon = new SymbolIcon(group.Group.Icon()),
                IsExpanded = group.Modules.Count > 0,
            };

            foreach (var module in group.Modules)
                // Tools with content open it from the menu; their settings are reached from Home → Utilities.
                groupItem.MenuItems.Add(NavItem(module.DisplayName, ModuleIcon.Create(module, imageSize: 18), (module as IModuleContent)?.ContentPageType ?? module.SettingsPageType));

            foreach (var extra in group.ExtraPages)
                groupItem.MenuItems.Add(NavItem(extra.Title, new SymbolIcon(extra.Icon), extra.PageType));

            if (groupItem.MenuItems.Count == 0)
            {
                groupItem.MenuItems.Add(new NavigationViewItem
                {
                    Content = "More tools coming soon",
                    IsEnabled = false,
                    Icon = new SymbolIcon(SymbolRegular.Clock24),
                });
            }

            Navigation.MenuItems.Add(groupItem);
        }

        // "Welcome to Helm" (WelcomePage) is hidden for now; re-add:
        // Navigation.FooterMenuItems.Add(NavItem("Welcome to Helm", SymbolRegular.HandWave24, typeof(WelcomePage)));
        Navigation.FooterMenuItems.Add(NavItem("What's new", SymbolRegular.Megaphone24, typeof(WhatsNewPage)));
        var feedback = new NavigationViewItem { Content = "Give feedback", Icon = new SymbolIcon(SymbolRegular.PersonFeedback24) };
        feedback.Click += (_, _) => _viewModel.OpenFeedbackCommand.Execute(null);
        Navigation.FooterMenuItems.Add(feedback);
    }

    private NavigationViewItem NavItem(string title, SymbolRegular icon, Type pageType) => NavItem(title, new SymbolIcon(icon), pageType);

    /// <summary>A menu entry that opens its page as a tab (or goes to its tab); no TargetPageType, so the NavigationView never navigates.</summary>
    private NavigationViewItem NavItem(string title, IconElement icon, Type pageType)
    {
        var item = new NavigationViewItem { Content = title, Icon = icon, Tag = pageType };
        item.Click += (_, _) => _tabs.Open(pageType, _tabs.Main);
        return item;
    }

    /// <summary>The menu marks the page of the main window's shown tab.</summary>
    private void HighlightMenu(Type? page)
    {
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>().Concat(Navigation.FooterMenuItems.OfType<NavigationViewItem>()))
        {
            var children = item.MenuItems.OfType<NavigationViewItem>().ToList();
            foreach (var child in children) child.IsActive = Equals(child.Tag, page);
            // A group shows as one icon while the menu is collapsed: it lights up for its tools.
            item.IsActive = Equals(item.Tag, page) || children.Any(c => c.IsActive);
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var results = _viewModel.Search(sender.Text);
        sender.ItemsSource = results;
        sender.IsSuggestionListOpen = results.Count > 0;
    }

    private void NavigateTo(SearchResult? result)
    {
        if (result is null) return;
        SearchBox.IsSuggestionListOpen = false;
        _services.GetRequiredService<IShellNavigator>().Navigate(result.PageType, result.Target);
    }
}
