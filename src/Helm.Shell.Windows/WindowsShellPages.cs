using Avalonia.Controls;
using Helm.BugReports.Ui;
using Helm.General;
using Helm.Home;
using Helm.Shell.BugReports;
using Helm.Shell.Tabs;
using Helm.Shell.Windows.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Shell.Windows;

/// <summary>
/// The shell's own pages on Windows: Home, General (with this PC's MCP part) and Bug reports, each the shared Avalonia
/// view from Helm.Shell.UI. The app's shell asks for them by key (<see cref="ShellPages"/>) the first time their tab opens.
/// </summary>
public sealed class WindowsShellPages(IServiceProvider services)
{
    /// <summary>The page for <paramref name="key"/>, or null when it is not one of the shell's own.</summary>
    public Control? Create(string key) => key switch
    {
        ShellPages.Home => services.GetRequiredService<HomeScreen>().CreateView(),
        ShellPages.General => General(),
        ShellPages.BugReports => new BugReportsView(services.GetRequiredService<BugReportsViewModel>(),
            new PcBugReportsPageHost(services.GetRequiredService<IPcBugReporting>())),
        _ => null,
    };

    private GeneralView General()
    {
        var model = services.GetRequiredService<GeneralComposer>().Model;
        McpPart.Attach(model, services.GetRequiredService<McpViewModel>());
        return new GeneralView(model);
    }
}
