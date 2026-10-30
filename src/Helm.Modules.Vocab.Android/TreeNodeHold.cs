using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Helm.Modules.Vocab;

/// <summary>
/// Holding a box of a tree of parts (挽 of 挽昼) opens a menu to make it one of the user's words, or to open it when
/// they have it. A finger that moves further than a few pixels is scrolling, not holding.
/// </summary>
internal sealed class TreeNodeHold
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(450);
    private const double Slop = 10;

    private readonly DispatcherTimer _timer = new() { Interval = HoldTime };
    private Button? _box;
    private Point _start;

    private TreeNodeHold(Control root)
    {
        _timer.Tick += (_, _) => Open();
        root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Cancel(), RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Cancel(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public static void Attach(Control root) => _ = new TreeNodeHold(root);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Cancel();
        if (e.Source is not Visual source) return;
        var box = source.FindAncestorOfType<Button>(includeSelf: true);
        if (box?.DataContext is not VocabNodeViewModel { CanAddAsWord: true } || !box.Classes.Contains("node")) return;
        _box = box;
        _start = e.GetPosition(box);
        _timer.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_box is null) return;
        var at = e.GetPosition(_box);
        if (Math.Abs(at.X - _start.X) > Slop || Math.Abs(at.Y - _start.Y) > Slop) Cancel();
    }

    private void Open()
    {
        _timer.Stop();
        if (_box is not { DataContext: VocabNodeViewModel node } box) return;
        _box = null;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = node.AddAsWordText, Command = node.AddAsWordCommand });
        menu.ShowAt(box);
    }

    private void Cancel()
    {
        _timer.Stop();
        _box = null;
    }
}
