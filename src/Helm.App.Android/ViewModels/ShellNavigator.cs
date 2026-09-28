using CommunityToolkit.Mvvm.ComponentModel;
using Helm.App.Android.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.ViewModels;

/// <summary>A tool's page: the view is created from <see cref="IAndroidModule.PageType"/> by the shell's data template.</summary>
public sealed record ModulePage(IAndroidModule Module);

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

    public void GoModule(IAndroidModule module) => Show(new ModulePage(module), module.DisplayName);

    /// <summary><see cref="IShellNavigation"/> for modules: <paramref name="pageType"/> is an Android module's page type.</summary>
    public void ShowPage(Type pageType)
    {
        var module = services.GetServices<IAndroidModule>().FirstOrDefault(m => m.PageType == pageType);
        if (module is not null) GoModule(module);
    }

    partial void OnCurrentPageChanged(object? value) => OnPropertyChanged(nameof(IsHome));

    private void Show(object page, string title)
    {
        CurrentPage = page;
        Title = title;
    }
}
