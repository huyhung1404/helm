using Avalonia.Controls;
using Helm.Modules.Habits.Ui;

namespace Helm.Modules.Habits;

/// <summary>
/// What only Windows does around the shared <see cref="HabitsView"/>: coming back to Helm while the page shows asks
/// again for the reasons of days skipped meanwhile (and shows days ticked elsewhere).
/// </summary>
internal sealed class WindowsHabitsPageHost : IHabitsPageHost
{
    private HabitsView? _view;
    private Window? _window;

    /// <summary>Follows the window <paramref name="view"/> is in (the tab may move between windows).</summary>
    public HabitsView Attach(HabitsView view)
    {
        _view = view;
        view.AttachedToVisualTree += (_, _) => Watch(TopLevel.GetTopLevel(view) as Window);
        view.DetachedFromVisualTree += (_, _) => Watch(null);
        return view;
    }

    public void Opened()
    {
    }

    private void Watch(Window? window)
    {
        if (ReferenceEquals(_window, window)) return;
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = window;
        if (_window is not null) _window.Activated += OnWindowActivated;
    }

    /// <summary>Back at Helm after a while (the page was left open): days ticked elsewhere, days skipped meanwhile.</summary>
    private void OnWindowActivated(object? sender, EventArgs e) => _view?.Shown();
}
