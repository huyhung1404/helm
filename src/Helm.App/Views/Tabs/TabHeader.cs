using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Helm.App.Views.Tabs;

/// <summary>
/// A tab on the strip: the page's icon and title, and a close button. The strip it sits on handles the clicks and drags
/// (see <see cref="TabHost"/>); a middle click closes it, the context menu moves it to a new window.
/// </summary>
internal sealed class TabHeader : Border
{
    private readonly ToolTab _tab;
    private readonly TextBlock _title;
    private readonly Border _indicator;
    private readonly Button _close;
    private bool _active;

    public TabHeader(ToolTab tab)
    {
        _tab = tab;
        Height = 32;
        MinWidth = 96;
        MaxWidth = 220;
        Margin = new Thickness(0, 0, 2, 0);
        Padding = new Thickness(10, 0, 2, 0);
        CornerRadius = new CornerRadius(6);
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;
        ToolTip = tab.Look.Title;
        AutomationProperties.SetName(this, tab.Look.Title);

        var icon = tab.Look.CreateIcon();
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 8, 0);

        _title = new TextBlock
        {
            Text = tab.Look.Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _close = new Button
        {
            Appearance = ControlAppearance.Transparent,
            Icon = new SymbolIcon(SymbolRegular.Dismiss12) { FontSize = 12 },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Close tab (Ctrl+W)",
        };
        AutomationProperties.SetName(_close, $"Close {tab.Look.Title}");
        _close.Click += (_, _) => _tab.Host?.Manager.Close(_tab);

        // The active tab is underlined in the accent colour.
        _indicator = new Border
        {
            Height = 2,
            Width = 16,
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _indicator.SetResourceReference(BackgroundProperty, "AccentFillColorDefaultBrush");

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_close, 2);
        row.Children.Add(icon);
        row.Children.Add(_title);
        row.Children.Add(_close);

        var layers = new Grid();
        layers.Children.Add(row);
        layers.Children.Add(_indicator);
        Child = layers;

        ContextMenuOpening += (_, _) => ContextMenu = BuildMenu();
        ContextMenu = new ContextMenu();
        MouseEnter += (_, _) => UpdateLook();
        MouseLeave += (_, _) => UpdateLook();
        UpdateLook();
    }

    public ToolTab Tab => _tab;

    public bool IsActive => _active;

    /// <summary>Opens the tab's menu (also for screen readers, through the tab's expand pattern).</summary>
    public void OpenMenu()
    {
        ContextMenu = BuildMenu();
        ContextMenu.PlacementTarget = this;
        ContextMenu.IsOpen = true;
    }

    public bool IsMenuOpen => ContextMenu?.IsOpen == true;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TabHeaderAutomationPeer(this);

    /// <summary>True when the press started on the close button (a click, not a drag).</summary>
    public bool IsOnCloseButton(object source) => source is DependencyObject d && IsWithin(d, _close);

    public void SetActive(bool active)
    {
        _active = active;
        UpdateLook();
    }

    private void UpdateLook()
    {
        _indicator.Visibility = _active ? Visibility.Visible : Visibility.Collapsed;
        _title.SetResourceReference(TextBlock.ForegroundProperty, _active ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        if (_active) SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        else if (IsMouseOver) SetResourceReference(BackgroundProperty, "SubtleFillColorTertiaryBrush");
        else Background = Brushes.Transparent;
        // Only the shown tab and the one under the mouse offer the close button; the others keep their room for the title.
        _close.Opacity = _active || IsMouseOver ? 1 : 0;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        if (_tab.Host is not { } host) return menu;
        var manager = host.Manager;

        var newWindow = new MenuItem { Header = "Move to new window", Icon = new SymbolIcon(SymbolRegular.WindowNew24) };
        newWindow.Click += (_, _) => manager.MoveToNewWindow(_tab);
        // The only tab of a window is already a window of its own; the main window always keeps one tab.
        newWindow.IsEnabled = host.Tabs.Count > 1;
        menu.Items.Add(newWindow);
        if (!ReferenceEquals(host, manager.Main))
        {
            var back = new MenuItem { Header = "Move to main window", Icon = new SymbolIcon(SymbolRegular.ArrowExit20) };
            back.Click += (_, _) => manager.Move(_tab, manager.Main, manager.Main.Tabs.Count);
            menu.Items.Add(back);
        }
        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "Close tab", InputGestureText = "Ctrl+W", Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        close.Click += (_, _) => manager.Close(_tab);
        menu.Items.Add(close);

        var others = new MenuItem { Header = "Close other tabs", IsEnabled = host.Tabs.Count > 1 };
        others.Click += (_, _) => manager.CloseOthers(_tab);
        menu.Items.Add(others);
        return menu;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? p = node; p is not null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }
}

/// <summary>A tab for screen readers and UI Automation: a tab item that can be selected (shown), and expanded to its menu.</summary>
internal sealed class TabHeaderAutomationPeer(TabHeader owner)
    : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner),
        System.Windows.Automation.Provider.ISelectionItemProvider, System.Windows.Automation.Provider.IExpandCollapseProvider
{
    protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
        System.Windows.Automation.Peers.AutomationControlType.TabItem;

    protected override string GetClassNameCore() => nameof(TabHeader);

    public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface) =>
        patternInterface is System.Windows.Automation.Peers.PatternInterface.SelectionItem or System.Windows.Automation.Peers.PatternInterface.ExpandCollapse
            ? this
            : base.GetPattern(patternInterface);

    public ExpandCollapseState ExpandCollapseState => owner.IsMenuOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

    public void Expand() => owner.OpenMenu();

    public void Collapse()
    {
        if (owner.ContextMenu is { } menu) menu.IsOpen = false;
    }

    public bool IsSelected => owner.IsActive;

    public System.Windows.Automation.Provider.IRawElementProviderSimple? SelectionContainer => null;

    public void Select()
    {
        if (owner.Tab.Host is { } host) host.Manager.Show(owner.Tab);
    }

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
    }
}
