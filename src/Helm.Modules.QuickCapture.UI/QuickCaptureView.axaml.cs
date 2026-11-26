using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.QuickCapture.Ui;

/// <summary>What the app does around the shared Quick Capture page (Windows: recording a shortcut).</summary>
public interface IQuickCapturePageHost
{
    /// <summary>
    /// A field that records a global shortcut (Helm.Ui.Windows' HotkeyBox), starting at <paramref name="gesture"/>.
    /// Null where there is none (the Gallery): the page then shows the keys only.
    /// </summary>
    Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) => null;
}

/// <summary>
/// Quick Capture's page (see the .axaml): the tool has no content of its own (its box floats over any app), so its
/// settings are its page, the on/off switch first. It is also what Home → Utilities opens.
/// </summary>
public partial class QuickCaptureView : UserControl
{
    private const double Narrow = 720;
    private readonly QuickCaptureViewModel? _viewModel;
    private readonly IModule? _module;
    private readonly IQuickCapturePageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public QuickCaptureView() => InitializeComponent();

    public QuickCaptureView(QuickCaptureViewModel viewModel, IQuickCapturePageHost host)
    {
        _viewModel = viewModel;
        _module = viewModel.Module;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
        // The tool's on/off switch (the first card).
        EnableSwitch.IsCheckedChanged += (_, _) => _module.IsEnabled = EnableSwitch.IsChecked == true;
        Refresh();

        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnViewModelChanged;
            _module.PropertyChanged += OnModuleChanged;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            _module.PropertyChanged -= OnModuleChanged;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickCaptureViewModel.Hotkey)) Dispatcher.UIThread.Post(Refresh);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IModule.IsEnabled) or nameof(IModule.StatusMessage)) Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>What follows the tool and the settings: the switch, "Open now", the problem, the shortcut field.</summary>
    private void Refresh()
    {
        if (_viewModel is not { } vm || _module is null) return;
        EnableSwitch.IsChecked = _module.IsEnabled;
        TryButton.IsEnabled = _module.IsEnabled;
        ProblemText.Text = _module.StatusMessage;
        ProblemCard.IsVisible = _module.IsEnabled && !string.IsNullOrWhiteSpace(_module.StatusMessage);
        if (ActivationHotkey.Tag is HotkeyGesture shown && shown == vm.Hotkey) return;
        ActivationHotkey.Content = Picker(vm.Hotkey, g =>
        {
            ActivationHotkey.Tag = g; // the recorder already shows it: keep it (and its focus)
            vm.Hotkey = g;
        });
        ActivationHotkey.Tag = vm.Hotkey;
    }

    /// <summary>The shortcut field: the app's recorder where there is one, else the keys as they are.</summary>
    private Control Picker(HotkeyGesture gesture, Action<HotkeyGesture> changed)
    {
        const string name = "Activation shortcut";
        if (_host?.CreateHotkeyPicker(gesture, changed) is not { } picker) return HotkeyKeys.Field(gesture, name);
        picker.HorizontalAlignment = HorizontalAlignment.Stretch;
        Avalonia.Automation.AutomationProperties.SetName(picker, name);
        return picker;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
    }
}
