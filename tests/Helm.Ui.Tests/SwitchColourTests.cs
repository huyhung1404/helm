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
}
