using System.Windows.Controls;
using Helm.General;
using Helm.Shell.Windows.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Shell.Windows;

/// <summary>General, in parts: the shared Avalonia <see cref="GeneralView"/> inside this WPF page, with this PC's MCP part.</summary>
public partial class GeneralPage : Page
{
    public GeneralPage(IServiceProvider services)
    {
        InitializeComponent();
        Host.Show(() =>
        {
            var model = services.GetRequiredService<GeneralComposer>().Model;
            // Avalonia has started here (inside Show): the MCP view can be made.
            McpPart.Attach(model, services.GetRequiredService<McpViewModel>());
            return new GeneralView(model);
        });
    }
}
