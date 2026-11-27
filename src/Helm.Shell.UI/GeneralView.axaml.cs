using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Core.Ui;

namespace Helm.General;

/// <summary>
/// General in parts, the same view on every platform. Wide: the list of parts on the left, the chosen one on the right.
/// Narrow (a phone): the list is the page; a part opens in its place and Back returns.
/// </summary>
public partial class GeneralView : UserControl
{
    private const double Narrow = 720;
    private bool _narrow;
    private bool _showingPart;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public GeneralView() => InitializeComponent();

    public GeneralView(GeneralViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Refresh();
        // Show what is redrawn: a view concern, for this window and this run only (Ctrl+Alt+Shift+R does the same).
        RedrawSwitch.IsCheckedChanged += (_, _) => RenderDebug.Set(TopLevel.GetTopLevel(this), RedrawSwitch.IsChecked == true);
        AttachedToVisualTree += (_, _) => RedrawSwitch.IsChecked = RenderDebug.IsOn(TopLevel.GetTopLevel(this));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", _narrow);
        Page.Margin = _narrow ? new Thickness(16, 18, 16, 0) : new Thickness(40, 28, 40, 0);
        Apply();
    }

    private void Apply()
    {
        if (_narrow)
        {
            Page.ColumnDefinitions = ColumnDefinitions.Parse("*");
            Grid.SetColumn(Detail, 0);
            Rail.IsVisible = !_showingPart;
            Detail.IsVisible = _showingPart;
            BackRow.IsVisible = true;
        }
        else
        {
            Page.ColumnDefinitions = ColumnDefinitions.Parse("290,*");
            Grid.SetColumn(Detail, 1);
            Rail.IsVisible = true;
            Detail.IsVisible = true;
            BackRow.IsVisible = false;
        }
    }

    private void Section_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralViewModel vm || (sender as Control)?.DataContext is not GeneralSection section) return;
        vm.Selected = section;
        Scroller.Offset = default;
        _showingPart = true;
        Apply();
        if (_narrow) Motion.Enter(Detail);
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        _showingPart = false;
        Apply();
        Motion.Enter(Rail);
    }

    /// <summary>Android Back: from a part to the list. False when the list is already shown.</summary>
    public bool HandleBack()
    {
        if (!_narrow || !_showingPart) return false;
        Back_Click(null, new RoutedEventArgs());
        return true;
    }
}
