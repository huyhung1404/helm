using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;

namespace Helm.Modules.AlwaysOnTop.Ui;

/// <summary>What the app does around the shared Always On Top page (Windows: recording a shortcut).</summary>
public interface IAlwaysOnTopPageHost
{
    /// <summary>
    /// A field that records a global shortcut (Helm.Ui.Windows' HotkeyBox), starting at <paramref name="gesture"/>.
    /// Null where there is none (the Gallery): the page then shows the keys only.
    /// </summary>
    Control? CreateHotkeyPicker(HotkeyGesture gesture, Action<HotkeyGesture> changed) => null;
}

/// <summary>
/// Always On Top's page (see the .axaml): the tool's on/off switch first, the shortcut, the border, the windows on top
/// now and the excluded apps. It is also the tool's settings page (Home → Utilities): there is nothing else to show.
/// </summary>
public partial class AlwaysOnTopView : UserControl
{
    private const double Narrow = 720;
    private readonly AlwaysOnTopViewModel? _viewModel;
    private readonly IModule? _module;
    private readonly IAlwaysOnTopPageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public AlwaysOnTopView() => InitializeComponent();

    public AlwaysOnTopView(AlwaysOnTopViewModel viewModel, IAlwaysOnTopPageHost host)
    {
        _viewModel = viewModel;
        _module = viewModel.Module;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();

        // The tool's on/off switch (the first card).
        EnableSwitch.IsChecked = _module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => _module.IsEnabled = EnableSwitch.IsChecked == true;
        HotkeyReset.Click += (_, _) => viewModel.Hotkey = viewModel.DefaultHotkey;
        // The excluded apps are saved when the box loses focus, not on every letter.
        ExcludedBox.Text = viewModel.ExcludedApps;
        ExcludedBox.LostFocus += (_, _) => viewModel.ExcludedApps = ExcludedBox.Text ?? "";

        Swatches.Children.AddRange(viewModel.BorderColors.Select(Swatch));
        Refresh();

        AttachedToVisualTree += (_, _) =>
        {
            _module.PropertyChanged += OnModule;
            viewModel.PropertyChanged += OnViewModel;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _module.PropertyChanged -= OnModule;
            viewModel.PropertyChanged -= OnViewModel;
        };
    }

    private void OnModule(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IModule.IsEnabled) or nameof(IModule.StatusMessage)) Dispatcher.UIThread.Post(Refresh);
    }

    private void OnViewModel(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AlwaysOnTopViewModel.Hotkey) or nameof(AlwaysOnTopViewModel.BorderColor) or nameof(AlwaysOnTopViewModel.PinnedSummary))
            Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>What follows the tool and the settings: the switch, the problem, the eyebrow, the shortcut field, the chosen colour.</summary>
    private void Refresh()
    {
        if (_viewModel is not { } vm || _module is null) return;
        EnableSwitch.IsChecked = _module.IsEnabled;
        ProblemText.Text = _module.StatusMessage;
        ProblemCard.IsVisible = _module.IsEnabled && !string.IsNullOrWhiteSpace(_module.StatusMessage);

        var keys = vm.Hotkey.IsEmpty ? "no shortcut" : vm.Hotkey.ToString();
        TopLine.Text = _module.IsEnabled ? $"ANY WINDOW · {vm.PinnedSummary.ToUpperInvariant()}" : "ANY WINDOW · TURNED OFF";
        EmptyText.Text = _module.IsEnabled
            ? $"Nothing is kept on top. Click a window, then press {keys}."
            : "Turn Always On Top on above, then press the shortcut on any window.";

        if (HotkeyPicker.Tag is not HotkeyGesture shown || shown != vm.Hotkey)
        {
            HotkeyPicker.Content = Picker(vm.Hotkey, g =>
            {
                HotkeyPicker.Tag = g; // the field already shows it: keep it (and its focus)
                vm.Hotkey = g;
            });
            HotkeyPicker.Tag = vm.Hotkey;
        }

        foreach (var swatch in Swatches.Children.OfType<Button>())
            swatch.Classes.Set("selected", string.Equals(swatch.Tag as string, vm.BorderColor, StringComparison.OrdinalIgnoreCase));
    }

    private Button Swatch(string color)
    {
        var button = new Button
        {
            Classes = { "chip", "swatch" },
            Tag = color,
            Content = new Border { Classes = { "fill" }, Width = 24, Height = 24, Background = Brush.Parse(color) },
        };
        ToolTip.SetTip(button, color);
        Avalonia.Automation.AutomationProperties.SetName(button, $"Border colour {color}");
        button.Click += (_, _) => _viewModel?.PickColorCommand.Execute(color);
        return button;
    }

    /// <summary>The shortcut field: the app's recorder where there is one, else the keys as they are.</summary>
    private Control Picker(HotkeyGesture gesture, Action<HotkeyGesture> changed)
    {
        const string name = "Keep on top shortcut";
        var picker = _host?.CreateHotkeyPicker(gesture, changed);
        if (picker is not null)
        {
            picker.HorizontalAlignment = HorizontalAlignment.Stretch;
            Avalonia.Automation.AutomationProperties.SetName(picker, name);
            return picker;
        }
        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (gesture.IsEmpty && gesture.Modifiers == HotkeyModifiers.None)
            keys.Children.Add(new TextBlock { Text = "No shortcut", Classes = { "muted" } });
        else
            foreach (var cap in gesture.ToKeycaps())
                keys.Children.Add(new Border { Classes = { "key" }, Child = new TextBlock { Text = cap } });
        var box = new Border
        {
            MinHeight = 40,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0),
            Child = keys,
        };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("HelmGlassStroke"));
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        ToolTip.SetTip(box, gesture.ToString());
        return box;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(16, 18, 16, 28) : new Thickness(40, 28, 40, 40);
    }
}
