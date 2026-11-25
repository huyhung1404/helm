using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Helm.Home;

/// <summary>
/// Helm's Home page, the same view on Windows, Android, macOS and iOS: one layout that narrows for a phone (below 720 px:
/// the state tiles stack, the tools go two by two).
/// </summary>
public partial class HomeView : UserControl
{
    private const double Narrow = 720;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public HomeView() => InitializeComponent();

    public HomeView(HomeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => Arrange(Bounds.Width);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Arrange(e.NewSize.Width);
    }

    private void Arrange(double width)
    {
        var narrow = width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        // A platform without shortcuts has two state tiles, not three.
        var tiles = (DataContext as HomeViewModel)?.ShowsShortcuts == false ? 2 : 3;
        States.Columns = narrow ? 1 : tiles;
        if (ToolList.ItemsPanelRoot is UniformGrid grid) grid.Columns = narrow ? 2 : width < 1000 ? 3 : 4;
    }
}
