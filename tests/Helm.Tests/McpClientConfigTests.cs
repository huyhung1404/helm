using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Settings;

namespace Helm.Tests;

/// <summary>The AI &amp; MCP page's client configs: the snippets, and reading only Helm's entry of a client's file.</summary>
public sealed class McpClientConfigTests
{
    private const string Exe = @"C:\Program Files\Helm App\current\Helm.exe";
    private static readonly string[] Args = ["--mcp", "--data-dir", @"C:\Users\user\Helm test data"];

    [Fact]
    public void VsCodeSnippetIsJsonWithTheCommandAndArguments()
    {
        var helm = JsonNode.Parse(McpClientConfig.VsCodeJson(Exe, Args))!["servers"]!["helm"]!;
        Assert.Equal("stdio", helm["type"]!.GetValue<string>());
        Assert.Equal(Exe, helm["command"]!.GetValue<string>());
        Assert.Equal(Args, helm["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void ClaudeDesktopSnippetIsJsonWithTheCommandAndArguments()
    {
        var helm = JsonNode.Parse(McpClientConfig.ClaudeDesktopJson(Exe, Args))!["mcpServers"]!["helm"]!;
        Assert.Equal(Exe, helm["command"]!.GetValue<string>());
        Assert.Equal(Args, helm["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void InstanceSnippetsAndAddArgumentsUseThisHelm()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(Path.Combine(dir.Path, "data with spaces"));
        using var settings = new SettingsStoreFactory(paths);
        var config = new McpClientConfig(paths, settings);

        var vs = JsonNode.Parse(config.VsCodeSnippet)!["servers"]!["helm"]!;
        Assert.Equal(config.Executable, vs["command"]!.GetValue<string>());
        Assert.Equal(["--mcp", "--data-dir", paths.Root], vs["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(["mcp", "add", "--scope", "user", "helm", "--", config.Executable, "--mcp", "--data-dir", paths.Root], config.ClaudeCodeAddArguments);
        Assert.Equal(["mcp", "remove", "helm", "--scope", "user"], McpClientConfig.ClaudeCodeRemoveArguments);
        Assert.Contains($"\"{paths.Root}\"", config.ClaudeCodeCommand);
    }

    [Fact]
    public void HelmPresentWithThisCommandIsAdded()
    {
        using var dir = new TempDir();
        var file = Write(dir, "mcp.json", McpClientConfig.VsCodeJson(Exe, Args));
        Assert.Equal(McpClientState.Added, McpClientConfig.ReadState(file, "servers", Exe, Args));
    }

    [Fact]
    public void HelmWithAnotherExecutableOrArgumentsIsAddedElsewhere()
    {
        using var dir = new TempDir();
        var moved = Write(dir, "a.json", McpClientConfig.ClaudeDesktopJson(@"D:\Old\Helm.exe", Args));
        Assert.Equal(McpClientState.AddedElsewhere, McpClientConfig.ReadState(moved, "mcpServers", Exe, Args));
        var otherArgs = Write(dir, "b.json", McpClientConfig.ClaudeDesktopJson(Exe, ["--mcp"]));
        Assert.Equal(McpClientState.AddedElsewhere, McpClientConfig.ReadState(otherArgs, "mcpServers", Exe, Args));
    }

    [Fact]
    public void HelmAbsentOrFileMissingIsNotAdded()
    {
        using var dir = new TempDir();
        var others = Write(dir, "c.json", """{ "mcpServers": { "other": { "command": "other.exe" } } }""");
        Assert.Equal(McpClientState.NotAdded, McpClientConfig.ReadState(others, "mcpServers", Exe, Args));
        // VS Code's section is "servers": a helm under mcpServers does not count there.
        var wrongSection = Write(dir, "d.json", McpClientConfig.ClaudeDesktopJson(Exe, Args));
        Assert.Equal(McpClientState.NotAdded, McpClientConfig.ReadState(wrongSection, "servers", Exe, Args));
        Assert.Equal(McpClientState.NotAdded, McpClientConfig.ReadState(Path.Combine(dir.Path, "missing.json"), "mcpServers", Exe, Args));
        Assert.Equal(McpClientState.NotAdded, McpClientConfig.ReadState(Write(dir, "empty.json", "  \r\n"), "mcpServers", Exe, Args));
    }

    [Fact]
    public void InvalidJsonIsUnreadable()
    {
        using var dir = new TempDir();
        Assert.Equal(McpClientState.Unreadable, McpClientConfig.ReadState(Write(dir, "e.json", "{ \"mcpServers\": "), "mcpServers", Exe, Args));
        Assert.Equal(McpClientState.Unreadable, McpClientConfig.ReadState(Write(dir, "f.json", "[1, 2]"), "mcpServers", Exe, Args));
    }

    [Fact]
    public void VsCodeCommentsTrailingCommasAndByteOrderMarkAreRead()
    {
        using var dir = new TempDir();
        var jsonc = "// my servers\n{ \"servers\": { \"helm\": { \"type\": \"stdio\", \"command\": " +
                    System.Text.Json.JsonSerializer.Serialize(Exe) + ", \"args\": [\"--mcp\"], }, }, }";
        var file = Path.Combine(dir.Path, "mcp.json");
        File.WriteAllText(file, jsonc, new System.Text.UTF8Encoding(true));
        Assert.Equal(McpClientState.Added, McpClientConfig.ReadState(file, "servers", Exe, ["--mcp"]));
    }

    [Fact]
    public void OtherServersSecretsNeverLeaveTheReader()
    {
        using var dir = new TempDir();
        const string secret = "sk-test-0000-never-shown";
        var file = Write(dir, ".claude.json", $$"""
            {
              "oauthAccount": { "emailAddress": "user@example.com" },
              "mcpServers": {
                "github": { "command": "gh-mcp", "env": { "GITHUB_TOKEN": "{{secret}}" } },
                "helm": { "type": "stdio", "command": {{System.Text.Json.JsonSerializer.Serialize(Exe)}}, "args": ["--mcp"] }
              }
            }
            """);
        var folders = new McpClientFolders(dir.Path, Path.Combine(dir.Path, "AppData"), Path.Combine(dir.Path, "Local"), null);
        Assert.Equal(file, McpClientConfig.ConfigFile(McpClientKind.ClaudeCode, folders));
        var state = McpClientConfig.ReadState(file, "mcpServers", Exe, ["--mcp"]);
        Assert.Equal(McpClientState.Added, state);

        using var settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(dir.Path, "data")));
        var status = new McpClientConfig(new HelmPaths(Path.Combine(dir.Path, "data")), settings).Status(McpClientKind.ClaudeCode, folders);
        Assert.DoesNotContain(secret, status.ToString());
        Assert.DoesNotContain("example.com", status.ToString());
        Assert.Equal(file, status.File);
    }

    [Fact]
    public void ConfigFilesFollowEachClient()
    {
        using var dir = new TempDir();
        var folders = new McpClientFolders(Path.Combine(dir.Path, "home"), Path.Combine(dir.Path, "Roaming"), Path.Combine(dir.Path, "Local"), null);
        Assert.Equal(Path.Combine(dir.Path, "Roaming", "Code", "User", "mcp.json"), McpClientConfig.ConfigFile(McpClientKind.VsCode, folders));
        Assert.Equal(Path.Combine(dir.Path, "Roaming", "Claude", "claude_desktop_config.json"), McpClientConfig.ConfigFile(McpClientKind.ClaudeDesktop, folders));
        Assert.Equal(Path.Combine(dir.Path, "cfg", ".claude.json"),
            McpClientConfig.ConfigFile(McpClientKind.ClaudeCode, folders with { ClaudeConfigDir = Path.Combine(dir.Path, "cfg") }));

        // Claude Desktop from the Microsoft Store keeps its config in its package folder.
        var store = Path.Combine(dir.Path, "Local", "Packages", "Claude_test", "LocalCache", "Roaming", "Claude");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "claude_desktop_config.json"), "{}");
        Assert.Equal(Path.Combine(store, "claude_desktop_config.json"), McpClientConfig.ConfigFile(McpClientKind.ClaudeDesktop, folders));
    }

    [Fact]
    public void FindClaudeTakesTheFirstOnPathThenTheNativeInstall()
    {
        using var dir = new TempDir();
        var first = Directory.CreateDirectory(Path.Combine(dir.Path, "first dir")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(dir.Path, "second")).FullName;
        File.WriteAllText(Path.Combine(second, "claude.cmd"), "");
        Assert.Equal(Path.Combine(second, "claude.cmd"), McpClientConfig.FindClaude($"relative;\"{first}\";{second}", dir.Path));
        File.WriteAllText(Path.Combine(first, "claude.exe"), "");
        Assert.Equal(Path.Combine(first, "claude.exe"), McpClientConfig.FindClaude($"{first};{second}", dir.Path));

        Assert.Null(McpClientConfig.FindClaude("", dir.Path));
        var native = Directory.CreateDirectory(Path.Combine(dir.Path, ".local", "bin")).FullName;
        File.WriteAllText(Path.Combine(native, "claude.exe"), "");
        Assert.Equal(Path.Combine(native, "claude.exe"), McpClientConfig.FindClaude("", dir.Path));
    }

    private static string Write(TempDir dir, string name, string content)
    {
        var file = Path.Combine(dir.Path, name);
        File.WriteAllText(file, content);
        return file;
    }
}
