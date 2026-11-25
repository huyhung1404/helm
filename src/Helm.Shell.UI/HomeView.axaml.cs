using Avalonia;
using Avalonia.Controls;

namespace Helm.Home;

/// <summary>Helm's Home page ("today"), the same view on Windows, Android, macOS and iOS; it narrows for a phone (below 720 px).</summary>
public partial class HomeView : UserControl
{
    private const double Narrow = 720;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public HomeView() => InitializeComponent();

    public HomeView(HomeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
    }
}
