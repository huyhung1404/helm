using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Habits.Ui;

/// <summary>Habits' settings, shown as a sheet over the page (see <see cref="HabitsView"/>'s gear).</summary>
public partial class HabitsSettingsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public HabitsSettingsView() => InitializeComponent();

    public HabitsSettingsView(HabitsViewModel viewModel, IModule module, Control? platformCards, Action close)
    {
        DataContext = viewModel;
        InitializeComponent();
        PlatformCards.Content = platformCards;
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        }
        AttachedToVisualTree += (_, _) => module.PropertyChanged += OnModule;
        DetachedFromVisualTree += (_, _) => module.PropertyChanged -= OnModule;
    }

    /// <summary>The cursor in "A new habit" (first run: the one step to take).</summary>
    public void FocusNewHabit() => NewHabitBox.Focus();

    // A habit's name is saved when its field loses focus, or on Enter.
    private void Rename_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
