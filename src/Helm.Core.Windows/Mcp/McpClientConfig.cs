using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Settings;

namespace Helm.Core.Mcp;

/// <summary>The clients the Claude &amp; MCP page knows how to connect.</summary>
public enum McpClientKind
{
    /// <summary>Claude Code in a terminal and in the VS Code extension (both read the user-scope <c>~/.claude.json</c>).</summary>
    ClaudeCode,

    /// <summary>VS Code's own MCP support (Copilot agent mode), the user <c>mcp.json</c>.</summary>
    VsCode,

    /// <summary>The Claude desktop app, <c>claude_desktop_config.json</c>.</summary>
    ClaudeDesktop,
}

public enum McpClientState
{
    /// <summary>The config file is missing, or has no <c>helm</c> entry.</summary>
    NotAdded,

    /// <summary>The <c>helm</c> entry starts this Helm with these arguments.</summary>
    Added,

    /// <summary>A <c>helm</c> entry starts another Helm.exe (Helm moved), or with other arguments.</summary>
    AddedElsewhere,

    /// <summary>The file is not JSON Helm can read (Helm never touches it).</summary>
    Unreadable,
}

/// <summary>Whether a client has Helm, and in which file it looked. Holds nothing else from that file.</summary>
public sealed record McpClientStatus(McpClientKind Kind, McpClientState State, string File);

/// <summary>The folders the clients keep their config in (a test passes its own).</summary>
public sealed record McpClientFolders(string UserProfile, string AppData, string LocalAppData, string? ClaudeConfigDir)
{
    public static McpClientFolders Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : null);
}

/// <summary>
/// How Claude reaches Helm's tools: the command it starts (this Helm.exe with --mcp), as a <c>claude mcp add</c> line
/// for Claude Code, as config snippets for VS Code and Claude Desktop, and whether each of them has Helm already.
/// </summary>
public sealed class McpClientConfig(HelmPaths paths, ISettingsStoreFactory settings)
{
    /// <summary>The name Helm has in every client's config.</summary>
    public const string ServerName = "helm";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public bool Enabled => settings.Get<McpSettings>(McpSettings.StoreId).Current.Enabled;

    /// <summary>The running Helm.exe (for an installed Helm, HelmApp\current\Helm.exe, which updates keep in place).</summary>
    public string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Helm.exe");

    /// <summary>"--mcp", plus the data folder when it is not the usual one (a test copy).</summary>
    public IReadOnlyList<string> Arguments =>
        string.Equals(Path.GetFullPath(paths.Root), Path.GetFullPath(new HelmPaths().Root), StringComparison.OrdinalIgnoreCase)
            ? ["--mcp"]
            : ["--mcp", "--data-dir", paths.Root];

    /// <summary>For a terminal: adds Helm to Claude Code for every folder (user scope).</summary>
    public string ClaudeCodeCommand => $"claude mcp add --scope user {ServerName} -- {Quote(Executable)} {string.Join(' ', Arguments.Select(Quote))}";

    /// <summary>What <c>claude</c> gets to add Helm (the same as <see cref="ClaudeCodeCommand"/>, as an argument list).</summary>
    public IReadOnlyList<string> ClaudeCodeAddArguments => ["mcp", "add", "--scope", "user", ServerName, "--", Executable, .. Arguments];

    /// <summary>What <c>claude</c> gets to remove Helm again.</summary>
    public static IReadOnlyList<string> ClaudeCodeRemoveArguments => ["mcp", "remove", ServerName, "--scope", "user"];

    /// <summary>VS Code's user <c>mcp.json</c> with only Helm in it.</summary>
    public string VsCodeSnippet => VsCodeJson(Executable, Arguments);

    /// <summary><c>claude_desktop_config.json</c> with only Helm in it.</summary>
    public string ClaudeDesktopSnippet => ClaudeDesktopJson(Executable, Arguments);

    public static string VsCodeJson(string command, IReadOnlyList<string> args) =>
        Wrap("servers", new JsonObject { ["type"] = "stdio", ["command"] = command, ["args"] = ToJsonArray(args) });

    public static string ClaudeDesktopJson(string command, IReadOnlyList<string> args) =>
        Wrap("mcpServers", new JsonObject { ["command"] = command, ["args"] = ToJsonArray(args) });

    /// <summary>Writes the config Helm's chats pass with --mcp-config; null while the tools are off.</summary>
    public string? WriteConfigFile()
    {
        if (!Enabled) return null;
        var folder = Path.Combine(paths.Root, "mcp");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "claude-mcp.json");
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = Executable,
                    ["args"] = ToJsonArray(Arguments),
                },
            },
        };
        File.WriteAllText(file, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    /// <summary>The file a client keeps its servers in. Claude Desktop from the Microsoft Store keeps it in its package folder.</summary>
    public static string ConfigFile(McpClientKind kind, McpClientFolders folders)
    {
        switch (kind)
        {
            case McpClientKind.ClaudeCode:
                return Path.Combine(folders.ClaudeConfigDir ?? folders.UserProfile, ".claude.json");
            case McpClientKind.VsCode:
                return Path.Combine(folders.AppData, "Code", "User", "mcp.json");
            default:
                var classic = Path.Combine(folders.AppData, "Claude", "claude_desktop_config.json");
                if (File.Exists(classic)) return classic;
                var packages = Path.Combine(folders.LocalAppData, "Packages");
                try
                {
                    var store = Directory.Exists(packages)
                        ? Directory.EnumerateDirectories(packages, "Claude_*")
                            .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"))
                            .FirstOrDefault(File.Exists)
                        : null;
                    return store ?? classic;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return classic;
                }
        }
    }

    /// <summary>Where VS Code's servers go (<c>servers</c>) or the others' (<c>mcpServers</c>).</summary>
    public static string Section(McpClientKind kind) => kind == McpClientKind.VsCode ? "servers" : "mcpServers";

    /// <summary>Whether <paramref name="kind"/> starts this Helm, read from its config file now.</summary>
    public McpClientStatus Status(McpClientKind kind, McpClientFolders? folders = null)
    {
        var file = ConfigFile(kind, folders ?? McpClientFolders.Current);
        return new McpClientStatus(kind, ReadState(file, Section(kind), Executable, Arguments), file);
    }

    /// <summary>
    /// Reads only <c>&lt;section&gt;.helm.command</c> and <c>.args</c> of <paramref name="file"/> and compares them with
    /// <paramref name="command"/> and <paramref name="args"/>. Nothing else of the file (other servers, their tokens)
    /// leaves this method; a parse error is reported without its message.
    /// </summary>
    public static McpClientState ReadState(string file, string section, string command, IReadOnlyList<string> args)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(file)) return McpClientState.NotAdded;
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return McpClientState.Unreadable;
        }
        var json = bytes.AsMemory();
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..]; // Notepad's byte order mark
        if (json.Span.Trim(" \t\r\n"u8).IsEmpty) return McpClientState.NotAdded;
        try
        {
            using var document = JsonDocument.Parse(json, Lenient);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return McpClientState.Unreadable;
            if (!document.RootElement.TryGetProperty(section, out var servers) || servers.ValueKind != JsonValueKind.Object
                || !servers.TryGetProperty(ServerName, out var helm) || helm.ValueKind != JsonValueKind.Object)
                return McpClientState.NotAdded;
            var registered = helm.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var registeredArgs = helm.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList()
                : [];
            return SamePath(registered, command) && registeredArgs.SequenceEqual(args) ? McpClientState.Added : McpClientState.AddedElsewhere;
        }
        catch (JsonException)
        {
            return McpClientState.Unreadable;
        }
    }

    /// <summary>
    /// The Claude Code CLI typing <c>claude</c> in a terminal runs: the first claude.exe or claude.cmd on PATH, else the
    /// native installer's <c>~\.local\bin\claude.exe</c>. Null when Claude Code is not installed.
    /// </summary>
    public static string? FindClaude(string? pathVariable = null, string? userProfile = null)
    {
        var dirs = (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'));
        foreach (var dir in dirs)
        {
            foreach (var name in (string[])["claude.exe", "claude.cmd"])
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, name);
                }
                catch (ArgumentException)
                {
                    continue; // malformed PATH entry
                }
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
        }
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        return File.Exists(native) ? native : null;
    }

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> args) => new(args.Select(a => (JsonNode)a).ToArray());

    private static string Wrap(string section, JsonObject helm) =>
        new JsonObject { [section] = new JsonObject { [ServerName] = helm } }.ToJsonString(Indented);

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
}
