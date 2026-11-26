using Avalonia.Controls;

namespace Helm.Shell.Windows.Mcp;

/// <summary>General › MCP on Windows; its DataContext is the <see cref="McpViewModel"/>.</summary>
internal partial class McpView : UserControl
{
    public McpView() => InitializeComponent();

    public McpView(McpViewModel viewModel)
        : this() => DataContext = viewModel;
}
