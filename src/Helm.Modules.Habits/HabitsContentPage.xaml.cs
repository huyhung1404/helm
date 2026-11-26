using System.Windows;
using System.Windows.Controls;
using Helm.Modules.Habits.Ui;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// Avalonia <see cref="HabitsView"/> inside this WPF page. Coming back to Helm while it shows asks again for the reasons
/// of days skipped meanwhile.
/// </summary>
public partial class HabitsContentPage : Page, IHabitsPageHost
{
    private HabitsView? _view;
    private Window? _window;

    public HabitsContentPage(HabitsModule module, HabitsViewModel viewModel)
    {
        InitializeComponent();
        Host.Show(() => _view = new HabitsView(module, viewModel, this));
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.Activated += OnWindowActivated;
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.Activated -= OnWindowActivated;
            _window = null;
        };
    }

    /// <summary>Back at Helm after a while (the page was left open): days ticked elsewhere, days skipped meanwhile.</summary>
    private void OnWindowActivated(object? sender, EventArgs e) => _view?.Shown();

    public void Opened()
    {
    }
}
