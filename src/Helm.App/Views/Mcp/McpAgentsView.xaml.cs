using System.Windows.Controls;

namespace Helm.App.Views.Mcp;

/// <summary>The Agents &amp; MCP part of the AI page; its DataContext is a <see cref="ViewModels.McpViewModel"/>.</summary>
internal partial class McpAgentsView : UserControl
{
    public McpAgentsView() => InitializeComponent();
}
