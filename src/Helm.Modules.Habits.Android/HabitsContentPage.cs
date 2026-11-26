using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Ui;
using Helm.Modules.Habits.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// <see cref="HabitsView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>). Coming back to
/// the app on it asks again for the reasons of days skipped meanwhile; Back closes its sheet first (see
/// <see cref="HabitsModule.HandleBack"/>).
/// </summary>
public sealed class HabitsContentPage : UserControl, IFullPage, IHabitsPageHost
{
    private readonly HabitsModule _module;
    private readonly HabitsView _view;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public HabitsContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<HabitsModule>(), HelmAndroidServices.Current.GetRequiredService<HabitsViewModel>())
    {
    }

    public HabitsContentPage(HabitsModule module, HabitsViewModel viewModel)
    {
        _module = module;
        _view = new HabitsView(module, viewModel, this);
        Content = _view;
    }

    public Action<Control>? OpenMenu => ShellChrome.OpenNavigation;

    /// <summary>Asks for notifications (Android 13+, once) and catches today's reminder.</summary>
    public void Opened() => _module.PageShown();

    // Pages are transient: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActivityHost.Resumed += OnResumed;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_view.Shown);
}
