using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Helm.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Helm.App.Views.Tabs;

/// <summary>
/// A window a tab was dragged out into: Helm's title bar and a tab strip of its own, without the main window's menu
/// and search (the strip's "+" opens any page). It closes with its last tab; closing it closes its tabs, and their
/// pages wait as they were for the next time they open.
/// </summary>
internal sealed class TabWindow : FluentWindow
{
    public const double TitleBarHeight = 40;

    private readonly TitleBar _titleBar;
    private readonly System.Windows.Controls.TextBlock _title;

    public TabWindow(TabManager manager)
    {
        Host = new TabHost(manager);
        MinWidth = 520;
        MinHeight = 400;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        ShowInTaskbar = true;

        // As on the main window: Helm's icon, then the shown tab's title.
        var icon = new System.Windows.Controls.Image { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        AppIcon.SetSize(icon, 16);
        _title = new System.Windows.Controls.TextBlock { Text = "Helm", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(icon);
        header.Children.Add(_title);
        _titleBar = new TitleBar { Height = TitleBarHeight, Header = header };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(Host, 1);
        root.Children.Add(_titleBar);
        root.Children.Add(Host);
        Content = root;

        Host.Changed += (_, _) => UpdateTitle();
        TabKeys.Attach(this, () => Host);
        manager.Register(Host);
        Closed += (_, _) =>
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            manager.Unregister(Host);
        };
        ApplicationThemeManager.Changed += OnThemeChanged;
        SourceInitialized += (_, _) => WindowBackgroundManager.UpdateBackground(this, ApplicationThemeManager.GetAppTheme(), WindowBackdropType.Mica);
    }

    public TabHost Host { get; }

    private void UpdateTitle()
    {
        var title = Host.ActiveTab?.Look.Title;
        Title = title is null ? "Helm" : $"{title} - Helm";
        _title.Text = Title;
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent) =>
        WindowBackgroundManager.UpdateBackground(this, theme, WindowBackdropType.Mica);
}

/// <summary>The browser's tab keys on a Helm window: Ctrl+Tab and Ctrl+Shift+Tab switch, Ctrl+W and Ctrl+F4 close.</summary>
internal static class TabKeys
{
    public static void Attach(Window window, Func<TabHost?> host)
    {
        window.PreviewKeyDown += (_, e) =>
        {
            if (host() is not { } tabs || Keyboard.Modifiers is not (ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))) return;
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Tab:
                    tabs.SelectNext(shift ? -1 : 1);
                    e.Handled = true;
                    break;
                case Key.W or Key.F4 when !shift && tabs.ActiveTab is { } active:
                    tabs.Manager.Close(active);
                    e.Handled = true;
                    break;
            }
        };
    }
}
