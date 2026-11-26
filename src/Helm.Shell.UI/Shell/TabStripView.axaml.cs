using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using Helm.Core.Ui;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Shell.Views;

/// <summary>
/// A window's tab strip (see <see cref="TabStripViewModel"/>): the pill fits its tabs and the "+", and scrolls them
/// sideways (wheel too) once the window is too narrow. Dragging is <see cref="TabDrag"/>; the menus are built here.
/// </summary>
public partial class TabStripView : UserControl
{
    private const double AddRoom = 32 + 2 + 8; // the "+" and the pill's padding

    public TabStripView()
    {
        InitializeComponent();
        Drag = new TabDrag(this, TabList, () => DataContext as TabStripViewModel);
        AddButton.Click += (_, _) => ShowLaunchMenu();
        Scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            Scroller.Offset = Scroller.Offset.WithX(Math.Max(0, Scroller.Offset.X - e.Delta.Y * 48));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        TabList.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
    }

    /// <summary>Reorders and tears off tabs; the shell hears when a tab leaves the strip.</summary>
    public TabDrag Drag { get; }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The pill hugs its tabs; the tabs scroll once they no longer fit beside the "+".
        Scroller.MaxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - AddRoom);
        return base.MeasureOverride(availableSize);
    }

    private void ShowLaunchMenu()
    {
        if (DataContext is not TabStripViewModel strip) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var page in strip.Launchable)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = page.Title,
                Icon = new ModuleIcon { Symbol = page.Glyph, Image = page.Icon, Size = 16 },
                Command = strip.LaunchCommand,
                CommandParameter = page,
            });
        }
        flyout.ShowAt(AddButton);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source || source.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tab")) is not { DataContext: TabItemViewModel tab } header) return;
        var flyout = new MenuFlyout();
        flyout.Items.Add(Item("Move to new window", Symbol.WindowNew, tab.MoveToNewWindowCommand, tab.CanMoveToNewWindow));
        if (tab.CanMoveToMainWindow) flyout.Items.Add(Item("Move to main window", Symbol.ArrowExit, tab.MoveToMainWindowCommand, true));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("Close tab", Symbol.Dismiss, tab.CloseCommand, true, "Ctrl+W"));
        flyout.Items.Add(Item("Close other tabs", null, tab.CloseOthersCommand, tab.CanCloseOthers));
        flyout.ShowAt(header, showAtPointer: true);
        e.Handled = true;
    }

    private static MenuItem Item(string header, Symbol? icon, System.Windows.Input.ICommand command, bool enabled, string? gesture = null) => new()
    {
        Header = header,
        Icon = icon is { } glyph ? new SymbolIcon { Symbol = glyph, FontSize = 16 } : null,
        Command = command,
        IsEnabled = enabled,
        InputGesture = gesture is null ? null : KeyGesture.Parse(gesture),
    };
}
