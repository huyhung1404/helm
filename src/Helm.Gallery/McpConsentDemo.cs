using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Helm.Core.Mcp;
using Helm.Core.Ui;
using Helm.Mcp.Ui;

namespace Helm.Gallery;

/// <summary>Which question an MCP consent Gallery entry asks.</summary>
internal enum McpConsentStage
{
    /// <summary>A shell command as root on a server, allowed for the session only for this exact command.</summary>
    RootCommand,
    /// <summary>A change to Helm's data while Helm runs as Administrator: plain, may be allowed for the session.</summary>
    Change,
    /// <summary>A dangerous call: the details prominent, never for the session.</summary>
    Dangerous,
}

/// <summary>The PC's consent window (the real view McpConsentWindow shows) over the backdrop, centred like on a monitor.</summary>
internal static class McpConsentDemo
{
    public static Control Create(McpConsentStage stage) => Build(stage).Screen;

    /// <summary>The screen and the view (the UI tests drive it).</summary>
    public static (Control Screen, McpConsentView View) Build(McpConsentStage stage)
    {
        var view = new McpConsentView(new McpConsentText(Prompt(stage)))
        {
            MaxWidth = 520,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return (new Panel { Children = { new Backdrop(), view } }, view);
    }

    public static McpConsentPrompt Prompt(McpConsentStage stage)
    {
        var fairy = new McpClientInfo(Guid.Parse("6f1c2b0e-0000-4000-8000-000000000001"), "Fairy", "1.4.2") { Elevated = false };
        return stage switch
        {
            McpConsentStage.RootCommand => new McpConsentPrompt(
                new McpConsentRequest("ssh_exec", McpRisk.Remote, "Run a shell command on server “fairy-box”",
                    "Runs the command below on fairy-box over SSH and gives Fairy what it prints.",
                    "An AI agent runs commands on another machine: Helm asks first every time.",
                    "sudo systemctl restart nginx && journalctl -u nginx -n 20 --no-pager", "fairy-box (root)", Elevated: true, McpDanger.Normal)
                {
                    Scope = "sudo systemctl restart nginx && journalctl -u nginx -n 20 --no-pager",
                    SessionWhenElevated = true,
                },
                fairy, OfferSession: true, HelmElevated: false, CallerNotElevated: false),
            McpConsentStage.Change => new McpConsentPrompt(
                new McpConsentRequest("tracker_add_task", McpRisk.Change, "Add a task to “Hôm nay”",
                    "Adds “Gọi điện cho mẹ” to the list Hôm nay, due tomorrow. It syncs to your other devices.",
                    "Helm runs as Administrator, so Helm asks before an AI agent changes your things.",
                    null, "Tracker › Hôm nay", Elevated: false, McpDanger.Normal),
                fairy, OfferSession: true, HelmElevated: true, CallerNotElevated: true),
            _ => new McpConsentPrompt(
                new McpConsentRequest("slack_post", McpRisk.Remote, "Post in #release-bubble-pop",
                    "Posts the message below in #release-bubble-pop as you, behind the AI signature.",
                    "What goes to Slack cannot be taken back: Helm asks each time.",
                    "Chốt build 2.3.1 lên store chiều nay nhé. QA đã pass regression.\n— sent by Fairy (AI)", "#release-bubble-pop", Elevated: false,
                    McpDanger.High),
                fairy, OfferSession: false, HelmElevated: false, CallerNotElevated: false),
        };
    }
}
