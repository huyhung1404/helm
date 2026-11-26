using Helm.General;

namespace Helm.Shell.Windows.Mcp;

/// <summary>
/// General › MCP on Windows: the part (before Help) showing <see cref="McpView"/>, its line in the list kept up to date,
/// and the clients' files and the tools read again each time the part is opened (they may have changed meanwhile).
/// </summary>
internal static class McpPart
{
    public static void Attach(GeneralViewModel model, McpViewModel mcp)
    {
        if (model.McpContent is not null) return;
        model.AttachMcp(new McpView(mcp));
        var section = model.Section("mcp");
        section.Summary = mcp.Summary;
        mcp.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(McpViewModel.Summary)) section.Summary = mcp.Summary;
        };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralViewModel.IsMcp) && model.IsMcp) mcp.Refresh();
        };
    }
}
