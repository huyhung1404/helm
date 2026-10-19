using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>): a wall of cards, and
/// the open note over it. While the tool is off the notes stay visible but read-only, with a note pointing to the
/// settings (Home → Utilities).
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
        viewModel.PropertyChanged += OnViewModelChanged;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        // Ctrl+S saves right away (it saves by itself a moment after typing anyway).
        InputBindings.Add(new KeyBinding(new SaveNowCommand(viewModel), Key.S, ModifierKeys.Control));
        // Esc closes the open note (saved), wherever the focus is in it.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !_viewModel.IsEditorOpen) return;
            _viewModel.CloseEditor();
            e.Handled = true;
        };
        // Leaving the page saves what was typed.
        Unloaded += (_, _) => _viewModel.Flush();
        ApplyEnabled();
    }

    /// <summary>A click beside the open note closes it, like Esc.</summary>
    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.CloseEditor();
        e.Handled = true;
    }

    /// <summary>The cursor goes into the note that opens, at the end of its text, so typing goes on where it stopped.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NotesViewModel.IsEditorOpen) || !_viewModel.IsEditorOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_viewModel.IsEditorOpen) return;
            BodyBox.Focus();
            BodyBox.CaretIndex = BodyBox.Text.Length;
        });
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        EditorLayer.IsEnabled = on;
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
