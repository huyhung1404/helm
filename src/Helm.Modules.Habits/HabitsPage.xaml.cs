using System.Windows;
using System.Windows.Input;
using Helm.Core.Services;
using Helm.Core.Ui;

namespace Helm.Modules.Habits;

/// <summary>Habits' settings (Home → Utilities). The habits themselves are on <see cref="HabitsContentPage"/>.</summary>
public partial class HabitsPage : ModulePageBase
{
    private readonly HabitsViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public HabitsPage(HabitsModule module, HabitsViewModel viewModel, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(HabitsContentPage));

    private void NewHabit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.AddHabitCommand.CanExecute(null)) return;
        _viewModel.AddHabitCommand.Execute(null);
        e.Handled = true;
    }
}
