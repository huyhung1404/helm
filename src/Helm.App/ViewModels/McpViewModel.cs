using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.App.ViewModels;

/// <summary>The Claude &amp; MCP page: how Claude Code and VS Code reach Helm's tools, and what they may do.</summary>
internal sealed partial class McpViewModel(McpPermissionsViewModel permissions) : ObservableObject
{
    public McpPermissionsViewModel Permissions { get; } = permissions;
}
