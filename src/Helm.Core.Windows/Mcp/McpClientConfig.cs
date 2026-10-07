using Helm.Core.Settings;

namespace Helm.Core.Mcp;

/// <summary>
/// How Claude reaches Helm's tools: the command it starts (this Helm.exe with --mcp), as an MCP config file for Helm's
/// own chats and as a <c>claude mcp add</c> line for Claude Code in a terminal.
/// </summary>
public sealed class McpClientConfig(HelmPaths paths, ISettingsStoreFactory settings)
{
    public bool Enabled => settings.Get<McpSettings>(McpSettings.StoreId).Current.Enabled;

    /// <summary>The running Helm.exe (for an installed Helm, HelmApp\current\Helm.exe, which updates keep in place).</summary>
    public string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Helm.exe");

    /// <summary>"--mcp", plus the data folder when it is not the usual one (a test copy).</summary>
    public IReadOnlyList<string> Arguments =>
        string.Equals(Path.GetFullPath(paths.Root), Path.GetFullPath(new HelmPaths().Root), StringComparison.OrdinalIgnoreCase)
            ? ["--mcp"]
            : ["--mcp", "--data-dir", paths.Root];

    /// <summary>For a terminal: adds Helm to Claude Code for every folder (user scope).</summary>
    public string ClaudeCodeCommand => $"claude mcp add --scope user helm -- {Quote(Executable)} {string.Join(' ', Arguments.Select(Quote))}";

    /// <summary>Writes the config Helm's chats pass with --mcp-config; null while the tools are off.</summary>
    public string? WriteConfigFile()
    {
        if (!Enabled) return null;
        var folder = Path.Combine(paths.Root, "mcp");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "claude-mcp.json");
        var config = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["helm"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = Executable,
                    ["args"] = new System.Text.Json.Nodes.JsonArray(Arguments.Select(a => (System.Text.Json.Nodes.JsonNode)a).ToArray()),
                },
            },
        };
        File.WriteAllText(file, config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
}
