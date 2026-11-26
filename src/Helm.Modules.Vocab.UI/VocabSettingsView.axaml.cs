using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Vocab.Ui;

/// <summary>Vocabulary's settings, shown as a sheet over the page (see <see cref="VocabView"/>'s gear).</summary>
public partial class VocabSettingsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public VocabSettingsView() => InitializeComponent();

    public VocabSettingsView(VocabViewModel viewModel, IModule module, IVocabPageHost host, Action close)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Below this the card gets hard to read over a busy app (the setting clamps to it too).
        OpacitySlider.Minimum = VocabSettingsViewModel.MinCardOpacity;
        PlatformCards.Content = host.CreatePlatformSettings();
        PlatformCards.IsVisible = PlatformCards.Content is not null;
        ShowNowButton.IsVisible = host.ShowWordNow is not null;
        ShowNowButton.Click += (_, _) => host.ShowWordNow?.Invoke();
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        }
        AttachedToVisualTree += (_, _) => module.PropertyChanged += OnModule;
        DetachedFromVisualTree += (_, _) => module.PropertyChanged -= OnModule;
    }
}
