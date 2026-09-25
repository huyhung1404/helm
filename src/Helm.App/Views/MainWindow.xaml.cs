using System.ComponentModel;
using System.Windows;
using Helm.App.Services;
using Helm.App.ViewModels;
using Helm.App.Views.Pages;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Helm.App.Views;

internal partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IServiceProvider _services;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly NavPaneController _navPane;

    public MainWindow(MainWindowViewModel viewModel, IServiceProvider services, ShellNavigator navigator, ISettingsStoreFactory settings)
    {
        _viewModel = viewModel;
        _services = services;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        DataContext = viewModel;
        InitializeComponent();

        RestorePlacement();
        _navPane = new NavPaneController(Navigation, RootGrid, _general, TitleIcon);
        Navigation.SetServiceProvider(services);
        BuildNavigation();
        navigator.Attach(Navigation, () => _services.GetRequiredService<ShellController>().ShowMainWindow());

        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.QuerySubmitted += (_, e) => NavigateTo(_viewModel.Search(e.QueryText).FirstOrDefault());
        SearchBox.SuggestionChosen += (_, e) => NavigateTo(e.SelectedItem as SearchResult);

        Loaded += (_, _) => Navigation.Navigate(StartupPage());
        Navigation.Navigated += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ConstrainPageWidth);
    }

    /// <summary>
    /// WPF-UI hosts pages in a scroll viewer that also scrolls horizontally, so a page is measured with infinite
    /// width and one long line (e.g. an update tile) makes the whole page wider than the window — cards then get
    /// clipped on the right. Disabling horizontal scrolling on the host makes every page fit the viewport.
    /// </summary>
    private void ConstrainPageWidth()
    {
        if (Navigation.SelectedItem is null) return;
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

    /// <summary>"--page Diagnostics" (dev/testing) opens that page instead of Home.</summary>
    private Type StartupPage()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return typeof(HomePage);
        var name = args[index + 1];
        var candidates = new[] { typeof(HomePage), typeof(GeneralPage), typeof(DiagnosticsPage), typeof(WelcomePage), typeof(WhatsNewPage) }
            .Concat(_viewModel.Groups.SelectMany(g => g.Modules).Select(m => m.SettingsPageType));
        return candidates.FirstOrDefault(t => t.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ?? typeof(HomePage);
    }

    /// <summary>Remembers size/position in general.json.</summary>
    public void SavePlacement()
    {
        // Before the window has ever been shown WPF reports Left/Top as NaN (CenterScreen) and RestoreBounds as
        // Rect.Empty (infinite); only persist real, finite placements.
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
                groupItem.MenuItems.Add(NavItem(module.DisplayName, module.Icon, module.SettingsPageType));

            foreach (var extra in group.ExtraPages)
                groupItem.MenuItems.Add(NavItem(extra.Title, extra.Icon, extra.PageType));

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

    private static NavigationViewItem NavItem(string title, SymbolRegular icon, Type pageType) => new()
    {
        Content = title,
        Icon = new SymbolIcon(icon),
        TargetPageType = pageType,
    };

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
