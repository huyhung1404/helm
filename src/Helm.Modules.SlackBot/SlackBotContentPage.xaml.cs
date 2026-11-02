using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;

namespace Helm.Modules.SlackBot;

/// <summary>
/// SlackBot itself, opened from the menu, the Quick access tile and the overlay (see <see cref="IModuleContent"/>).
/// While the tool is off, what it kept stays visible but read-only, with a note pointing to the settings.
/// </summary>
public partial class SlackBotContentPage : Page
{
    private readonly SlackBotModule _module;

    public SlackBotContentPage(SlackBotModule module, SlackBotInboxViewModel viewModel)
    {
        _module = module;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscription lives as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
