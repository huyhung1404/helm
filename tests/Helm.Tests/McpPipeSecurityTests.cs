using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Helm's MCP pipe: who may open it, how many at once, how long a message may be, and what it learns about the caller.</summary>
public sealed class McpPipeSecurityTests
{
    private static McpServer Server(int maxMessageLength = McpServer.DefaultMaxMessageLength) =>
        new([new McpTool("echo", "Echoes.", McpTool.NoArguments(), (_, _) => Task.FromResult<object?>("hi")) { ReadOnly = true }], "0.29.0", "test")
        {
            MaxMessageLength = maxMessageLength,
        };

    [Fact]
    public async Task The_pipe_is_owned_by_this_user_and_denies_network_logons()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        using var host = new McpPipeHost(paths, () => Server(), NullLogger<McpPipeHost>.Instance);
        host.Start();
        await using var pipe = await McpBridge.ConnectAsync(paths, 3000);
        var security = pipe.GetAccessControl();
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(me.User, security.GetOwner(typeof(SecurityIdentifier)));

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        Assert.Contains(rules, r => r.IdentityReference == network && r.AccessControlType == AccessControlType.Deny);
        // Allowed: this user only (no Everyone, no Users, no Administrators group).
        Assert.All(rules.Where(r => r.AccessControlType == AccessControlType.Allow), r => Assert.Equal(me.User, r.IdentityReference));
    }

    [Fact]
    public async Task The_server_learns_whether_the_caller_runs_elevated()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        McpServer? served = null;
        using var host = new McpPipeHost(paths, () => served = Server(), NullLogger<McpPipeHost>.Instance);
        host.Start();

        Assert.Contains("\"id\":1", await RoundTrip(paths, """{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
        // Both ends are this test process: same user, and the same elevation (CI runs the tests elevated).
        Assert.Equal(Environment.IsPrivilegedProcess, served!.Client.Elevated);
        Assert.False(served.Client.IsLessPrivilegedThan(Environment.IsPrivilegedProcess));
    }

    [Fact]
    public async Task Callers_over_the_limit_are_closed_at_once()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        using var host = new McpPipeHost(paths, () => Server(), NullLogger<McpPipeHost>.Instance, maxConnections: 2);
        host.Start();

        await using var first = await Open(paths);
        await using var second = await Open(paths);
        Assert.Equal(2, host.Connections.Count);

        await using var third = await McpBridge.ConnectAsync(paths, 3000);
        var read = await third.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read); // closed without an answer

        // The first two still work, and a place frees up when one leaves.
        Assert.Contains("\"id\":2", await Send(first, """{"jsonrpc":"2.0","id":2,"method":"ping"}"""));
        await second.DisposeAsync();
        await WaitUntil(() => host.Connections.Count == 1);
        await using var again = await Open(paths);
        Assert.Equal(2, host.Connections.Count);
    }

    [Fact]
    public async Task A_message_over_the_cap_gets_an_error_and_the_connection_goes_on()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        using var host = new McpPipeHost(paths, () => Server(maxMessageLength: 1000), NullLogger<McpPipeHost>.Instance);
        host.Start();
        await using var pipe = await McpBridge.ConnectAsync(paths, 3000);

        var huge = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" + new string('x', 50_000) + "\"}}";
        var reply = await Send(pipe, huge);
        Assert.Contains("Message too large", reply);
        Assert.Contains("-32600", reply);

        Assert.Contains("\"id\":2", await Send(pipe, """{"jsonrpc":"2.0","id":2,"method":"ping"}"""));
    }

    [Fact]
    public async Task Lines_are_read_with_crlf_and_a_long_last_line_without_a_newline()
    {
        var server = Server(maxMessageLength: 100);
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\r\n\r\n" + new string('y', 500) + "\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}\n" + new string('z', 300)));
        var output = new MemoryStream();
        await server.RunAsync(input, output, default);
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Contains("\"id\":1", lines[0]);
        Assert.Contains("too large", lines[1]);
        Assert.Contains("\"id\":2", lines[2]);
        Assert.Contains("too large", lines[3]);
    }

    private static async Task<NamedPipeClientStream> Open(HelmPaths paths)
    {
        var pipe = await McpBridge.ConnectAsync(paths, 3000);
        Assert.Contains("\"id\":0", await Send(pipe, """{"jsonrpc":"2.0","id":0,"method":"ping"}"""));
        return pipe;
    }

    private static async Task<string> RoundTrip(HelmPaths paths, string line)
    {
        await using var pipe = await McpBridge.ConnectAsync(paths, 3000);
        return await Send(pipe, line);
    }

    /// <summary>Writes one line and reads one line back (byte by byte, so the pipe can be used again).</summary>
    private static async Task<string> Send(Stream pipe, string line)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await pipe.FlushAsync();
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await pipe.ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 1 && one[0] != '\n') bytes.Add(one[0]);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }
}
