using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Helm.Modules.CommandPalette;

/// <summary>View glue for the palette: the arrows move through the results, Enter or a click opens one.</summary>
public partial class PalettePanel : UserControl
{
    private readonly PaletteViewModel _viewModel;

    public PalettePanel(PaletteViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Input.PreviewKeyDown += OnInputKeyDown;
        List.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(List, (System.Windows.DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: PaletteResult result })
                _viewModel.Choose(result);
        };
    }

    /// <summary>The window is shown again: fresh results, the old query selected so typing replaces it.</summary>
    public void Prepare()
    {
        _viewModel.Refresh();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Input.Focus();
            Keyboard.Focus(Input);
            Input.SelectAll();
        });
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                _viewModel.Move(1);
                ScrollToSelected();
                break;
            case Key.Up:
                e.Handled = true;
                _viewModel.Move(-1);
                ScrollToSelected();
                break;
            case Key.PageDown:
                e.Handled = true;
                _viewModel.Move(5);
                ScrollToSelected();
                break;
            case Key.PageUp:
                e.Handled = true;
                _viewModel.Move(-5);
                ScrollToSelected();
                break;
            case Key.Enter:
                e.Handled = true;
                _viewModel.Choose();
                break;
        }
    }

    private void ScrollToSelected()
    {
        if (_viewModel.Selected is { } selected) List.ScrollIntoView(selected);
    }
}
