using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.QuickCapture;

/// <summary>Quick Capture's settings page, in Avalonia like the other tools' pages (shown inside a WPF page through Helm.Ui.Windows).</summary>
public partial class QuickCaptureView : UserControl
{
    private readonly QuickCaptureViewModel? _viewModel;
    private readonly IModule? _module;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public QuickCaptureView() => InitializeComponent();

    public QuickCaptureView(QuickCaptureViewModel viewModel)
    {
        _viewModel = viewModel;
        _module = viewModel.Module;
        DataContext = viewModel;
        InitializeComponent();
        // The tool's on/off switch (the first card).
        EnableSwitch.IsChecked = _module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => _module.IsEnabled = EnableSwitch.IsChecked == true;
        TryButton.IsEnabled = _module.IsEnabled;
        _module.PropertyChanged += OnModuleChanged;
        // The shortcut the user records.
        ActivationHotkey.Gesture = viewModel.Hotkey;
        ActivationHotkey.PropertyChanged += (_, e) =>
        {
            if (e.Property == Helm.Ui.Windows.HotkeyBox.GestureProperty) viewModel.Hotkey = ActivationHotkey.Gesture;
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickCaptureViewModel.Hotkey) && ActivationHotkey.Gesture != viewModel.Hotkey) ActivationHotkey.Gesture = viewModel.Hotkey;
        };
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // A narrow window: the phone margins and the narrow class, as on every shared page.
        var narrow = e.NewSize.Width < 720;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Avalonia.Thickness(16, 18, 16, 28) : new Avalonia.Thickness(40, 28, 40, 40);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IModule.IsEnabled) || _module is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            EnableSwitch.IsChecked = _module.IsEnabled;
            TryButton.IsEnabled = _module.IsEnabled;
        });
    }
}
