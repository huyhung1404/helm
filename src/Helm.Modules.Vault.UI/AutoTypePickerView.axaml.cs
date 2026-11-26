using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault.Ui;

/// <summary>
/// View glue for auto-type's picker: the keyboard stays in the search box, Up and Down move the choice (the list follows
/// it), Enter types the chosen login.
/// </summary>
public partial class AutoTypePickerView : UserControl
{
    private readonly AutoTypePickerViewModel? _viewModel;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public AutoTypePickerView() => InitializeComponent();

    public AutoTypePickerView(AutoTypePickerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Tunnel: the text box would take Up, Down and Enter itself.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>The cursor goes into the search, so typing filters at once.</summary>
    public void FocusSearch() =>
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                _viewModel.Move(1);
                break;
            case Key.Up:
                e.Handled = true;
                _viewModel.Move(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                _viewModel.ChooseCommand.Execute(null);
                break;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AutoTypePickerViewModel.Selected) || _viewModel?.Selected is not { } row) return;
        // The chosen row stays in sight while Up and Down move through a long list.
        Dispatcher.UIThread.Post(() =>
        {
            var index = _viewModel.Rows.IndexOf(row);
            if (index >= 0 && List.ContainerFromIndex(index) is { } container) container.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
