using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Keyboard;

/// <summary>Helm Keyboard's settings page, in Avalonia like the other tools' pages (shown inside a WPF page through Helm.Ui.Windows).</summary>
public partial class KeyboardView : UserControl
{
    private readonly KeyboardViewModel? _viewModel;
    private readonly IModule? _module;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public KeyboardView() => InitializeComponent();

    public KeyboardView(KeyboardViewModel viewModel)
    {
        _viewModel = viewModel;
        _module = viewModel.Module;
        DataContext = viewModel;
        InitializeComponent();
        // The tool's on/off switch (the first card).
        EnableSwitch.IsChecked = _module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => _module.IsEnabled = EnableSwitch.IsChecked == true;
        // The shortcut the user records.
        OwnHotkey.Gesture = viewModel.Hotkey;
        OwnHotkey.PropertyChanged += (_, e) =>
        {
            if (e.Property == Helm.Ui.Windows.HotkeyBox.GestureProperty) viewModel.Hotkey = OwnHotkey.Gesture;
        };
        // The excluded apps are saved when the box loses focus, not on every letter.
        ExcludedBox.Text = viewModel.ExcludedApps;
        ExcludedBox.LostFocus += (_, _) => viewModel.ExcludedApps = ExcludedBox.Text ?? "";
        viewModel.PropertyChanged += OnViewModelChanged;
        _module.PropertyChanged += OnModuleChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.PropertyName == nameof(KeyboardViewModel.Hotkey) && OwnHotkey.Gesture != _viewModel.Hotkey) OwnHotkey.Gesture = _viewModel.Hotkey;
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled) && _module is not null)
            Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = _module.IsEnabled);
    }
}
