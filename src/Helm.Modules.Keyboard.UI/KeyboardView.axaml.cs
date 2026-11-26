using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Keyboard.Ui;

/// <summary>What the app does around the shared Helm Keyboard page (Windows: recording a shortcut).</summary>
public interface IKeyboardPageHost
{
    /// <summary>
    /// A field that records a global shortcut (Helm.Ui.Windows' HotkeyBox), starting at <paramref name="gesture"/>.
    /// Null where there is none (the Gallery): the page then shows the keys only.
    /// </summary>
    Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) => null;
}

/// <summary>
/// Helm Keyboard's page (see the .axaml): the tool has no content of its own, so its settings are its page, the on/off
/// switch first. It is also what Home → Utilities opens.
/// </summary>
public partial class KeyboardView : UserControl
{
    private const double Narrow = 720;
    private readonly KeyboardViewModel? _viewModel;
    private readonly IModule? _module;
    private readonly IKeyboardPageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public KeyboardView() => InitializeComponent();

    public KeyboardView(KeyboardViewModel viewModel, IKeyboardPageHost host)
    {
        _viewModel = viewModel;
        _module = viewModel.Module;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
        // The tool's on/off switch (the first card).
        EnableSwitch.IsCheckedChanged += (_, _) => _module.IsEnabled = EnableSwitch.IsChecked == true;
        // The excluded apps are saved when the box loses focus, not on every letter.
        ExcludedBox.Text = viewModel.ExcludedApps;
        ExcludedBox.LostFocus += (_, _) => viewModel.ExcludedApps = ExcludedBox.Text ?? "";
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
        if (e.PropertyName == nameof(KeyboardViewModel.Hotkey)) Dispatcher.UIThread.Post(Refresh);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IModule.IsEnabled) or nameof(IModule.StatusMessage)) Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>What follows the tool and the settings: the switch, the problem, the shortcut field.</summary>
    private void Refresh()
    {
        if (_viewModel is not { } vm || _module is null) return;
        EnableSwitch.IsChecked = _module.IsEnabled;
        ProblemText.Text = _module.StatusMessage;
        ProblemCard.IsVisible = !string.IsNullOrWhiteSpace(_module.StatusMessage);
        if (OwnHotkey.Tag is HotkeyGesture shown && shown == vm.Hotkey) return;
        OwnHotkey.Content = Picker(vm.Hotkey, g =>
        {
            OwnHotkey.Tag = g; // the recorder already shows it: keep it (and its focus)
            vm.Hotkey = g;
        });
        OwnHotkey.Tag = vm.Hotkey;
    }

    /// <summary>The shortcut field: the app's recorder where there is one, else the keys as they are.</summary>
    private Control Picker(HotkeyGesture gesture, Action<HotkeyGesture> changed)
    {
        const string name = "Your own shortcut";
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
