using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
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

    public static readonly DependencyProperty BodyFillsHeightProperty = DependencyProperty.Register(
        nameof(BodyFillsHeight), typeof(bool), typeof(ModulePageBase), new PropertyMetadata(false, (d, e) => ScrollViewer.SetCanContentScroll(d, !(bool)e.NewValue)));

    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) };
    private readonly Border _hero = new();
    private readonly TextBlock _description = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar _infoBar = new() { Severity = InfoBarSeverity.Warning, IsClosable = false, Margin = new Thickness(0, 0, 0, 12) };
    private readonly CardControl _enableCard = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly CardHeader _enableHeader = new();
    private readonly ToggleSwitch _enableToggle = new() { OnContent = "On", OffContent = "Off" };
    private readonly ContentPresenter _body = new();

    public ModulePageBase()
    {
        var hero = _hero;
        hero.Width = 96;
        hero.Height = 96;
        hero.CornerRadius = new CornerRadius(8);
        hero.Margin = new Thickness(0, 0, 20, 0);
        hero.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        // The icon (set with the module) inherits the accent colour when it is a symbol.
        hero.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");

        var intro = new DockPanel { Margin = new Thickness(0, 0, 0, 24), LastChildFill = true };
        DockPanel.SetDock(hero, Dock.Left);
        intro.Children.Add(hero);
        intro.Children.Add(_description);

        _enableCard.Header = _enableHeader;
        _enableCard.Content = _enableToggle;

        // A grid rather than a stack panel so the body can take the remaining height (BodyFillsHeight). Inside the
        // shell's scroll viewer the height is unbounded and the star row simply sizes to its content.
        var layout = new Grid { Margin = new Thickness(24, 16, 24, 24) };
        UIElement[] rows = [_title, intro, _infoBar, _enableCard, _body];
        for (var i = 0; i < rows.Length; i++)
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = i == rows.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(rows[i], i);
            layout.Children.Add(rows[i]);
        }
        base.Content = layout;
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

    /// <summary>
    /// For pages that manage their own scrolling (a chat, a log): the page is not wrapped in the shell's scroll viewer
    /// and <see cref="Body"/> fills the height left under the header. WPF-UI's NavigationViewContentPresenter sets
    /// IsDynamicScrollViewerEnabled from <see cref="ScrollViewer.CanContentScrollProperty"/>, which it overrides to
    /// true for every Page; this sets it to false for the page.
    /// </summary>
    public bool BodyFillsHeight
    {
        get => (bool)GetValue(BodyFillsHeightProperty);
        set => SetValue(BodyFillsHeightProperty, value);
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
        var heroIcon = ModuleIcon.Create(module, fontSize: 56, imageSize: 56);
        heroIcon.HorizontalAlignment = HorizontalAlignment.Center;
        heroIcon.VerticalAlignment = VerticalAlignment.Center;
        _hero.Child = heroIcon;
        _description.Text = module.Description;
        _enableCard.Icon = ModuleIcon.Create(module, imageSize: 24);
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
