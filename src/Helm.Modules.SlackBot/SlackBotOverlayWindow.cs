using Avalonia;
using Avalonia.Input;
using Helm.Core.Desktop;
using Helm.Modules.SlackBot.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Slack Report's list on top of every app: the shared <see cref="SlackBotListView"/> in a see-through Avalonia window of
/// its own (<see cref="AvaloniaCornerWindow"/>), in the bottom-right corner or wherever the user dragged it (by any spot
/// that is not a button; a double-click puts it back). It never takes the keyboard from the app in front, except while a
/// reply is written in it. Folded, it is a badge with the count; a click opens it again.
/// </summary>
public sealed class SlackBotOverlayWindow : ISlackBotListHost
{
    private readonly Action<bool> _folded;
    private readonly AvaloniaCornerWindow _window;
    private readonly SlackBotListView _view;

    /// <param name="anchor">Where the user put it last: its bottom-left corner; null for the bottom-right corner.</param>
    /// <param name="fold">Starts folded.</param>
    /// <param name="moved">Keeps the new bottom-left corner after a drag (null after going back to the corner).</param>
    /// <param name="folded">Keeps whether it is folded.</param>
    public SlackBotOverlayWindow(SlackBotInboxViewModel viewModel, IWindowService windows, IMonitorService monitors, Point? anchor, bool fold,
        Action<Point?> moved, Action<bool> folded)
    {
        _folded = folded;
        _window = new AvaloniaCornerWindow(windows, monitors, "Slack Report", null, anchor, moved);
        _view = new SlackBotListView(viewModel, this, SlackBotIcon.Image, fold)
        {
            Margin = new Thickness(AvaloniaCornerWindow.ShadowSpace),
        };
        _window.Content = _view;
        _window.Closed += (_, _) => _view.StopTyping();
    }

    /// <summary>How opaque it is while the mouse is not on it.</summary>
    public double RestOpacity
    {
        get => _view.RestOpacity;
        set => _view.RestOpacity = value;
    }

    public bool IsFolded => _view.IsFolded;

    public bool IsVisible => _window.IsVisible;

    /// <summary>Solid for a few seconds, to be seen (a reminder).</summary>
    public void Flash() => _view.Flash();

    public void ShowOverlay() => _window.Show();

    public void Hide()
    {
        _view.StopTyping();
        _window.Hide();
    }

    public void Close() => _window.Close();

    public void SetFolded(bool fold) => _view.SetFolded(fold);

    void ISlackBotListHost.Folded(bool folded) => _folded(folded);

    public void TakeKeyboard() => _window.TakeKeyboard();

    public void ReturnKeyboard() => _window.ReturnKeyboard();

    public bool Drag(PointerPressedEventArgs e) => _window.Drag(e);

    public void BackToCorner() => _window.BackToCorner();
}
