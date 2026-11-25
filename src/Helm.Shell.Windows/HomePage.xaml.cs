using System.Windows.Controls;
using Helm.Home;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Shell.Windows;

/// <summary>Helm's Home ("today"): the shared Avalonia <see cref="HomeView"/> inside this WPF page.</summary>
public partial class HomePage : Page
{
    public HomePage(IServiceProvider services)
    {
        InitializeComponent();
        Host.Show(() => services.GetRequiredService<HomeScreen>().CreateView());
    }
}
