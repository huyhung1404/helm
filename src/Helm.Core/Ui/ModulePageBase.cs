using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using Helm.Core.Modules;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Helm.Core.Ui;

/// <summary>
/// Shared template for every module settings tab: title, icon + description, a non-fatal InfoBar, the big
/// "Enable &lt;module&gt;" card, and then the page's own sections (<see cref="Body"/>), which are disabled while the
/// module is off. Derived XAML pages set <see cref="Module"/> and put their cards directly inside the element.
/// </summary>
[ContentProperty(nameof(Body))]
public class ModulePageBase : Page
{
    public static readonly DependencyProperty ModuleProperty = DependencyProperty.Register(
        nameof(Module), typeof(IHelmModule), typeof(ModulePageBase), new PropertyMetadata(null, (d, _) => ((ModulePageBase)d).OnModuleChanged()));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(ModulePageBase), new PropertyMetadata(null, (d, e) => ((ModulePageBase)d)._body.Content = e.NewValue));

    public static readonly DependencyProperty ExtraMessageProperty = DependencyProperty.Register(
        nameof(ExtraMessage), typeof(string), typeof(ModulePageBase), new PropertyMetadata(null, (d, _) => ((ModulePageBase)d).UpdateInfoBar()));

    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) };
    private readonly SymbolIcon _heroIcon = new() { FontSize = 56 };
    private readonly TextBlock _description = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar _infoBar = new() { Severity = InfoBarSeverity.Warning, IsClosable = false, Margin = new Thickness(0, 0, 0, 12) };
    private readonly CardControl _enableCard = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly CardHeader _enableHeader = new();
    private readonly ToggleSwitch _enableToggle = new() { OnContent = "On", OffContent = "Off" };
    private readonly ContentPresenter _body = new();

    public ModulePageBase()
    {
        var hero = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 20, 0),
            Child = _heroIcon,
        };
        hero.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        _heroIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _heroIcon.VerticalAlignment = VerticalAlignment.Center;
        _heroIcon.SetResourceReference(ForegroundProperty, "AccentTextFillColorPrimaryBrush");

        var intro = new DockPanel { Margin = new Thickness(0, 0, 0, 24), LastChildFill = true };
        DockPanel.SetDock(hero, Dock.Left);
        intro.Children.Add(hero);
        intro.Children.Add(_description);

        _enableCard.Header = _enableHeader;
        _enableCard.Content = _enableToggle;

        var stack = new StackPanel { Margin = new Thickness(0, 0, 24, 24) };
        stack.Children.Add(_title);
        stack.Children.Add(intro);
        stack.Children.Add(_infoBar);
        stack.Children.Add(_enableCard);
        stack.Children.Add(_body);
        base.Content = stack;
    }

    public IHelmModule? Module
    {
        get => (IHelmModule?)GetValue(ModuleProperty);
        set => SetValue(ModuleProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>Optional extra warning (e.g. a hotkey conflict) shown in the InfoBar in addition to the module status.</summary>
    public string? ExtraMessage
    {
        get => (string?)GetValue(ExtraMessageProperty);
        set => SetValue(ExtraMessageProperty, value);
    }

    private void OnModuleChanged()
    {
        if (Module is not { } module) return;

        _title.Text = module.DisplayName;
        Title = module.DisplayName;
        _heroIcon.Symbol = module.Icon;
        _description.Text = module.Description;
        _enableCard.Icon = new SymbolIcon(module.Icon);
        _enableHeader.Title = $"Enable {module.DisplayName}";

        _enableToggle.SetBinding(ToggleSwitch.IsCheckedProperty, new Binding(nameof(IHelmModule.IsEnabled)) { Source = module, Mode = BindingMode.TwoWay });
        _body.SetBinding(IsEnabledProperty, new Binding(nameof(IHelmModule.IsEnabled)) { Source = module });

        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IHelmModule.StatusMessage)) Dispatcher.BeginInvoke(UpdateInfoBar);
        };
        UpdateInfoBar();
    }

    private void UpdateInfoBar()
    {
        var parts = new[] { Module?.StatusMessage, ExtraMessage }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        _infoBar.Title = parts.Length > 0 ? "Heads up" : string.Empty;
        _infoBar.Message = string.Join(Environment.NewLine, parts);
        _infoBar.IsOpen = parts.Length > 0;
        _infoBar.Visibility = parts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
