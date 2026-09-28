using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Helm.App.Android.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.App.Android.Views;

/// <summary>Shows a tool: resolves a new instance of the page it asks for (<see cref="ModulePage.PageType"/>) from DI.</summary>
public sealed class ModulePageTemplate : IDataTemplate
{
    public bool Match(object? data) => data is ModulePage;

    public Control? Build(object? param)
    {
        if (param is not ModulePage page) return null;
        try
        {
            return (Control)App.Services.GetRequiredService(page.PageType);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<ModulePageTemplate>>().LogError(ex, "Could not open the page of {Module}", page.Module.Id);
            return new TextBlock { Text = $"{page.Module.DisplayName} could not be opened.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        }
    }
}
