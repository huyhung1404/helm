using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace Helm.Ui.Tests;

public sealed class SwitchColourTests
{
    [AvaloniaFact]
    public void Switches_are_helm_teal_not_the_system_accent()
    {
        var window = new Window { Content = new ToggleSwitch { IsChecked = true } };
        window.Show();
        Assert.True(window.TryFindResource("ToggleSwitchFillOn", ThemeVariant.Dark, out var dark));
        Assert.Equal(Color.Parse("#2FD3C1"), ((ISolidColorBrush)dark!).Color);
        Assert.True(window.TryFindResource("ToggleSwitchFillOn", ThemeVariant.Light, out var light));
        Assert.Equal(Color.Parse("#0E9688"), ((ISolidColorBrush)light!).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void A_switch_takes_the_background_colour_where_it_sits()
    {
        var topLeft = new ToggleSwitch { IsChecked = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var bottomRight = new ToggleSwitch { IsChecked = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        var window = new Window { Width = 800, Height = 600, Content = new Panel { Children = { topLeft, bottomRight } } };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var a = ((ISolidColorBrush)topLeft.Resources["ToggleSwitchFillOn"]!).Color;
        var b = ((ISolidColorBrush)bottomRight.Resources["ToggleSwitchFillOn"]!).Color;
        // Blue in the top left corner, teal in the bottom right.
        Assert.True(a.B > a.G, $"top left {a}");
        Assert.True(b.G > b.R && Math.Abs(b.G - b.B) < 40, $"bottom right {b}");
        Assert.NotEqual(a, b);
        window.Close();
    }
}
