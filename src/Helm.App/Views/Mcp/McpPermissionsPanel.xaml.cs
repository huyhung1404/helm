using System.Windows.Controls;

namespace Helm.App.Views.Mcp;

/// <summary>The Permissions card of the AI page (Agents &amp; MCP); its DataContext is a <see cref="ViewModels.McpPermissionsViewModel"/>.</summary>
internal partial class McpPermissionsPanel : UserControl
{
    public McpPermissionsPanel() => InitializeComponent();
}
