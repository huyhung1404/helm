using System.Text.Json.Nodes;
using Helm.Core.Services;

namespace Helm.Tests;

/// <summary>Workspace Controller's old hooks come out of Claude Code's settings; nothing else is touched.</summary>
public sealed class LegacyClaudeHooksTests
{
    private const string Settings = """
        {
          "permissions": { "allow": ["Bash(git status)"] },
          "hooks": {
            "Stop": [
              { "hooks": [ { "type": "http", "url": "http://127.0.0.1:51234/helm/workspace/hook", "timeout": 3 } ] },
              { "hooks": [ { "type": "command", "command": "notify-send done" } ] }
            ],
            "PermissionRequest": [
              { "matcher": "*", "hooks": [ { "type": "http", "url": "http://127.0.0.1:51234/helm/workspace/hook", "timeout": 1800 } ] }
            ],
            "PreToolUse": [
              { "hooks": [ { "type": "http", "url": "https://example.com/helm/workspace/hook" } ] }
            ]
          }
        }
        """;

    [Fact]
    public void Only_the_old_loopback_hooks_are_removed()
    {
        var after = JsonNode.Parse(LegacyClaudeHooks.Remove(Settings)!)!.AsObject();

        Assert.Equal("Bash(git status)", after["permissions"]!["allow"]![0]!.GetValue<string>());
        var hooks = after["hooks"]!.AsObject();
        // The event that only held the old hook is gone; the user's own hook and a non-loopback URL stay.
        Assert.False(hooks.ContainsKey("PermissionRequest"));
        Assert.Equal("notify-send done", Assert.Single(hooks["Stop"]!.AsArray())!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Single(hooks["PreToolUse"]!.AsArray());
    }

    [Fact]
    public void A_file_without_the_old_hooks_is_not_rewritten()
    {
        Assert.Null(LegacyClaudeHooks.Remove("""{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "x" } ] } ] } }"""));
        Assert.Null(LegacyClaudeHooks.Remove("{}"));
        Assert.Null(LegacyClaudeHooks.Remove(""));
    }
}
