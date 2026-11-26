using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Helm.Shell.Views;

/// <summary>
/// The collapsed menu (icon rail) slides open over the page once the mouse has rested on it, or on a trigger such as
/// the title bar's icon, for a moment, and closes a moment after the mouse has left both, as the old WPF menu did. Never
/// while pinned or on a phone (a drawer there).
/// </summary>
public sealed class MenuPeek
{
    public static readonly TimeSpan OpenAfter = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan CloseAfter = TimeSpan.FromMilliseconds(400);

    private readonly NavMenuView _menu;
    private readonly Func<NavMenuViewModel?> _model;
    private readonly DispatcherTimer _timer = new();
    private readonly HashSet<Control> _over = [];

    public MenuPeek(NavMenuView menu, Func<NavMenuViewModel?> model)
    {
        _menu = menu;
        _model = model;
        _timer.Tick += (_, _) => Settle();
        Watch(menu);
    }

    /// <summary>Hovering <paramref name="trigger"/> opens the rail too.</summary>
    public void Watch(Control trigger)
    {
        trigger.PointerEntered += (_, _) => Over(trigger, true);
        trigger.PointerExited += (_, _) => Over(trigger, false);
    }

    private void Over(Control control, bool over)
    {
        if (over) _over.Add(control);
        else _over.Remove(control);
        if (_model() is not { IsPinned: false, IsDrawer: false } model)
        {
            _timer.Stop();
            return;
        }
        var wanted = _over.Count > 0;
        if (wanted == model.IsPeeking)
        {
            _timer.Stop();
            return;
        }
        _timer.Interval = wanted ? OpenAfter : CloseAfter;
        _timer.Stop();
        _timer.Start();
    }

    private void Settle()
    {
        _timer.Stop();
        if (_model() is not { IsPinned: false, IsDrawer: false } model) return;
        if (_menu.IsResizing) return;
        model.IsPeeking = _over.Count > 0;
    }
}
