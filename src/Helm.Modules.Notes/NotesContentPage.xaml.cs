using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the notes stay visible but read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class NotesContentPage : Page
{
    private readonly NotesModule _module;
    private readonly NotesViewModel _viewModel;

    public NotesContentPage(NotesModule module, NotesViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.EditorFocusRequested += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => BodyBox.Focus());
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        // Ctrl+S saves right away (it saves by itself a moment after typing anyway).
        InputBindings.Add(new KeyBinding(new SaveNowCommand(viewModel), Key.S, ModifierKeys.Control));
        Loaded += (_, _) => _viewModel.OpenLastNote();
        // Leaving the page saves what was typed.
        Unloaded += (_, _) => _viewModel.Flush();
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class SaveNowCommand(NotesViewModel viewModel) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => viewModel.Flush();
    }
}
