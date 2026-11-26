using Avalonia;
using Avalonia.Controls;

namespace Helm.Shell.Pages;

/// <summary>
/// Welcome to Helm, the first run (see the .axaml): the three steps, only the first one active, and one button that
/// takes you to it (on the PC: Home, with its Utilities).
/// </summary>
public partial class WelcomeView : UserControl
{
    private const double Narrow = 720;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public WelcomeView() => InitializeComponent();

    public WelcomeView(Action start)
    {
        InitializeComponent();
        StartButton.Click += (_, _) => start();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
        Steps.Columns = narrow ? 1 : 3;
    }
}
