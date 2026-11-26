using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;

namespace Helm.Modules.WindowZones.Ui;

/// <summary>Window Zones' settings, shown as a sheet over the page (see <see cref="WindowZonesView"/>'s gear).</summary>
public partial class WindowZonesSettingsView : UserControl
{
    private readonly WindowZonesViewModel? _viewModel;
    private readonly IWindowZonesPageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public WindowZonesSettingsView() => InitializeComponent();

    public WindowZonesSettingsView(WindowZonesViewModel viewModel, IModule module, IWindowZonesPageHost host, Action close)
    {
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();

        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(IModule.IsEnabled) or nameof(IModule.StatusMessage))
                Dispatcher.UIThread.Post(() =>
                {
                    EnableSwitch.IsChecked = module.IsEnabled;
                    ShowProblem(module);
                });
        }
        ShowProblem(module);

        NextReset.Click += (_, _) => viewModel.NextWindowHotkey = viewModel.DefaultNextWindowHotkey;
        PreviousReset.Click += (_, _) => viewModel.PreviousWindowHotkey = viewModel.DefaultPreviousWindowHotkey;
        EditorReset.Click += (_, _) => viewModel.EditorHotkey = viewModel.DefaultEditorHotkey;
        Pickers();

        AttachedToVisualTree += (_, _) =>
        {
            module.PropertyChanged += OnModule;
            viewModel.PropertyChanged += OnViewModel;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            module.PropertyChanged -= OnModule;
            viewModel.PropertyChanged -= OnViewModel;
        };
    }

    private void ShowProblem(IModule module)
    {
        ProblemText.Text = module.StatusMessage;
        ProblemCard.IsVisible = module.IsEnabled && !string.IsNullOrWhiteSpace(module.StatusMessage);
    }

    private void OnViewModel(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WindowZonesViewModel.NextWindowHotkey) or nameof(WindowZonesViewModel.PreviousWindowHotkey)
            or nameof(WindowZonesViewModel.EditorHotkey))
            Dispatcher.UIThread.Post(Pickers);
    }

    /// <summary>The three shortcut fields: the app's recorder where there is one, else the keys as they are.</summary>
    private void Pickers()
    {
        if (_viewModel is not { } vm || _host is null) return;
        NextPicker.Content = Picker(vm.NextWindowHotkey, g => vm.NextWindowHotkey = g, "Next window");
        PreviousPicker.Content = Picker(vm.PreviousWindowHotkey, g => vm.PreviousWindowHotkey = g, "Previous window");
        EditorPicker.Content = Picker(vm.EditorHotkey, g => vm.EditorHotkey = g, "Zone editor shortcut");
    }

    private Control Picker(HotkeyGesture gesture, Action<HotkeyGesture> changed, string name)
    {
        var picker = _host?.CreateHotkeyPicker(gesture, changed);
        if (picker is not null)
        {
            picker.HorizontalAlignment = HorizontalAlignment.Stretch;
            Avalonia.Automation.AutomationProperties.SetName(picker, name);
            return picker;
        }
        return Keys(gesture, name);
    }

    /// <summary>The shortcut as keycaps in a field-shaped box (no recorder on this platform).</summary>
    private static Control Keys(HotkeyGesture gesture, string name)
    {
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
        // A phone sheet: next and previous one under the other.
        var narrow = e.NewSize.Width < 440;
        Grid.SetColumnSpan(NextField, narrow ? 2 : 1);
        Grid.SetColumn(PreviousField, narrow ? 0 : 1);
        Grid.SetRow(PreviousField, narrow ? 1 : 0);
        Grid.SetColumnSpan(PreviousField, narrow ? 2 : 1);
    }
}
