using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Helm.Core.Modules;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Core.Ui;

/// <summary>
/// The Android counterpart of the Windows ModulePageBase: icon + description, a warning InfoBar for
/// <see cref="IModule.StatusMessage"/>, the "Enable &lt;tool&gt;" card, and then the page's own sections (its content),
/// which are disabled while the tool is off. The shell's app bar already shows the title.
/// <code>
/// &lt;ui:ModulePageBase xmlns:ui="using:Helm.Core.Ui" Module="{Binding Module}"&gt;
///     &lt;StackPanel&gt; …sections… &lt;/StackPanel&gt;
/// &lt;/ui:ModulePageBase&gt;
/// </code>
/// </summary>
public class ModulePageBase : ContentControl
{
    public static readonly StyledProperty<IAndroidModule?> ModuleProperty =
        AvaloniaProperty.Register<ModulePageBase, IAndroidModule?>(nameof(Module));

    /// <summary>Optional extra warning shown in the InfoBar in addition to the module status.</summary>
    public static readonly StyledProperty<string?> ExtraMessageProperty =
        AvaloniaProperty.Register<ModulePageBase, string?>(nameof(ExtraMessage));

    private Frame? _frame;
    private IAndroidModule? _subscribed;

    public ModulePageBase()
    {
        Template = new FuncControlTemplate<ModulePageBase>((page, scope) => page.BuildFrame(scope));
    }

    public IAndroidModule? Module
    {
        get => GetValue(ModuleProperty);
        set => SetValue(ModuleProperty, value);
    }

    public string? ExtraMessage
    {
        get => GetValue(ExtraMessageProperty);
        set => SetValue(ExtraMessageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModuleProperty) Subscribe(VisualRoot is null ? null : Module);
        else if (change.Property == ExtraMessageProperty) UpdateFrame();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Module);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The module is a singleton: a page that is gone must not stay subscribed to it.
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    private Control BuildFrame(INameScope scope)
    {
        var heroIcon = new ModuleIcon { Size = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        heroIcon.Bind(ForegroundProperty, heroIcon.GetResourceObservable("SystemAccentColor"));
        var hero = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 16, 0), Child = heroIcon };
        hero.Bind(Border.BackgroundProperty, hero.GetResourceObservable("HelmSubtleFill"));
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var intro = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(hero, Dock.Left);
        intro.Children.Add(hero);
        intro.Children.Add(description);

        var infoText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var infoBar = new Border { Child = infoText, IsVisible = false, Classes = { "infobar", "warning" } };

        var enableIcon = new ModuleIcon { Size = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var enableTitle = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (Module is { } module && toggle.IsChecked is bool on && module.IsEnabled != on) module.IsEnabled = on;
        };
        var enableRow = new DockPanel();
        DockPanel.SetDock(enableIcon, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        enableRow.Children.Add(enableIcon);
        enableRow.Children.Add(toggle);
        enableRow.Children.Add(enableTitle);

        var presenter = new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            [!ContentPresenter.ContentProperty] = this[!ContentProperty],
            [!ContentPresenter.ContentTemplateProperty] = this[!ContentTemplateProperty],
        };
        presenter.RegisterInNameScope(scope);

        _frame = new Frame(heroIcon, description, infoBar, infoText, enableIcon, enableTitle, toggle, presenter);
        UpdateFrame();
        return new StackPanel
        {
            Children = { intro, infoBar, new Border { Child = enableRow, Classes = { "card" } }, presenter },
        };
    }

    private void Subscribe(IAndroidModule? module)
    {
        if (!ReferenceEquals(module, _subscribed))
        {
            if (_subscribed is not null) _subscribed.PropertyChanged -= OnModulePropertyChanged;
            _subscribed = module;
            if (module is not null) module.PropertyChanged += OnModulePropertyChanged;
        }
        UpdateFrame();
    }

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Modules may change state from a background thread (e.g. a sync callback).
        if (Dispatcher.UIThread.CheckAccess()) UpdateFrame();
        else Dispatcher.UIThread.Post(UpdateFrame);
    }

    private void UpdateFrame()
    {
        if (_frame is not { } f) return;
        var module = Module;
        f.HeroIcon.Symbol = f.EnableIcon.Symbol = module?.Icon ?? Symbol.Apps;
        f.HeroIcon.Image = f.EnableIcon.Image = module?.IconImage;
        f.Description.Text = module?.Description;
        f.EnableTitle.Text = module is null ? "" : $"Enable {module.DisplayName}";
        f.Toggle.IsChecked = module?.IsEnabled ?? false;
        f.Presenter.IsEnabled = module?.IsEnabled ?? false;

        var message = string.Join("\n", new[] { module?.StatusMessage, ExtraMessage }.Where(m => !string.IsNullOrWhiteSpace(m)));
        f.InfoText.Text = message;
        f.InfoBar.IsVisible = message.Length > 0;
    }

    private sealed record Frame(
        ModuleIcon HeroIcon, TextBlock Description, Border InfoBar, TextBlock InfoText,
        ModuleIcon EnableIcon, TextBlock EnableTitle, ToggleSwitch Toggle, ContentPresenter Presenter);
}
