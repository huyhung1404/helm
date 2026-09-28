using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.ViewModels;

/// <summary>
/// A tool's page: <see cref="PageType"/> is its settings page (<see cref="IAndroidModule.PageType"/>) or its content
/// page (<see cref="IModuleContent.ContentPageType"/>); the shell's data template creates it from DI.
/// </summary>
public sealed record ModulePage(IAndroidModule Module, Type PageType);

/// <summary>
/// Which page the shell shows. Pages are view models (Home, General, <see cref="ModulePage"/>); MainView's data
/// templates create the views, so a recreated activity simply builds them again from the same state.
/// </summary>
public sealed partial class ShellNavigator(IServiceProvider services) : ObservableObject, IShellNavigation
{
    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _title = "Home";

    public bool IsHome => CurrentPage is HomeViewModel;

    public void GoHome() => Show(services.GetRequiredService<HomeViewModel>(), "Home");

    public void GoGeneral() => Show(services.GetRequiredService<GeneralViewModel>(), "General");

    /// <summary>The tool's settings page (Home → Utilities).</summary>
    public void GoModule(IAndroidModule module) => Show(new ModulePage(module, module.PageType), module.DisplayName);

    /// <summary>What the drawer and Quick access open: the content page when the tool has one, else its settings.</summary>
    public void GoModuleContent(IAndroidModule module) =>
        Show(new ModulePage(module, (module as IModuleContent)?.ContentPageType ?? module.PageType), module.DisplayName);

    /// <summary><see cref="IShellNavigation"/> for modules: a module's settings or content page type.</summary>
    public void ShowPage(Type pageType)
    {
        foreach (var module in services.GetServices<IAndroidModule>())
        {
            if (module.PageType == pageType || (module as IModuleContent)?.ContentPageType == pageType)
            {
                Show(new ModulePage(module, pageType), module.DisplayName);
                return;
            }
        }
    }

    partial void OnCurrentPageChanged(object? value) => OnPropertyChanged(nameof(IsHome));

    private void Show(object page, string title)
    {
        CurrentPage = page;
        Title = title;
    }
}
