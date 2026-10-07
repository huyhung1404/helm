using System.Text.Json;
using Helm.Modules.Ssh;

namespace Helm.Tests;

/// <summary>What a menu and a server say about Claude (MCP): <c>readOnly</c>, <c>agents</c> and <see cref="SshHost.AllowMcp"/>.</summary>
public sealed class SshMenuAgentsTests
{
    private static MenuDescription Menu(string items) =>
        SshMenu.ParseDescription($$"""{ "protocol": 1, "groups": [ { "title": "Ops", "items": [ {{items}} ] } ] }""");

    [Fact]
    public void Items_are_not_read_only_and_are_offered_to_claude_by_default()
    {
        var item = Menu("""{ "id": "deploy", "title": "Deploy" }""").Find("deploy")!;
        Assert.False(item.ReadOnly);
        Assert.True(item.Agents);
    }

    [Fact]
    public void Read_only_and_agents_are_read()
    {
        var menu = Menu("""
            { "id": "status", "readOnly": true },
            { "id": "passwd", "agents": false },
            { "id": "both", "readOnly": false, "agents": true }
            """);
        Assert.Equal((true, true), (menu.Find("status")!.ReadOnly, menu.Find("status")!.Agents));
        Assert.Equal((false, false), (menu.Find("passwd")!.ReadOnly, menu.Find("passwd")!.Agents));
        Assert.Equal((false, true), (menu.Find("both")!.ReadOnly, menu.Find("both")!.Agents));
    }

    [Theory]
    [InlineData("""{ "id": "x", "agents": "false" }""", "agents")]
    [InlineData("""{ "id": "x", "agents": 0 }""", "agents")]
    [InlineData("""{ "id": "x", "agents": null }""", "agents")]
    [InlineData("""{ "id": "x", "readOnly": "yes" }""", "readOnly")]
    [InlineData("""{ "id": "x", "readOnly": [] }""", "readOnly")]
    public void A_wrong_type_is_refused(string item, string field)
    {
        var ex = Assert.Throws<MenuFormatException>(() => Menu(item));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Claude_may_not_use_a_server_until_it_is_allowed()
    {
        Assert.False(new SshHost().AllowMcp);
        // An older settings file has no such field.
        var old = JsonSerializer.Deserialize<SshHost>("""{ "Name": "web", "Address": "example.com", "Port": 2222, "User": "user" }""")!;
        Assert.False(old.AllowMcp);
        var allowed = JsonSerializer.Deserialize<SshHost>(JsonSerializer.Serialize(old with { AllowMcp = true }))!;
        Assert.True(allowed.AllowMcp);
    }
}
