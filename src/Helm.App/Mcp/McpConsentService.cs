using Avalonia.Threading;
using Helm.App.Shell;
using Helm.Core.Desktop;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Helm.Shell.Windows.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.App.Mcp;

/// <summary>
/// Puts the consent policy's questions to the user (<see cref="McpConsentPolicy"/> decides when to ask). The question
/// (<see cref="McpConsentWindow"/>) opens on top of everything, centred on the monitor under the mouse, whether Helm's
/// window is shown or hidden in the tray; while Helm is hidden a notification says the agent asks too, and clicking it
/// brings the question to the front. Nothing is ever allowed without a click on an Allow button.
/// </summary>
internal sealed class McpConsentService(IServiceProvider services, IUserNotifications notifications, IWindowService windows, IMonitorService monitors,
    ILogger<McpConsentService> logger) : IMcpConsentPrompt
{
    public async Task<McpConsentAnswer> AskAsync(McpConsentPrompt prompt, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<McpConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        McpConsentWindow? window = null;

        void Open()
        {
            if (answer.Task.IsCompleted) return;
            if (window is not null)
            {
                window.Activate();
                return;
            }
            window = new McpConsentWindow(prompt, windows, monitors);
            window.Closed += (_, given) => answer.TrySetResult(given);
            window.Show();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                Open();
            }
            catch (Exception ex)
            {
                // No question on screen means no: never allow by default.
                logger.LogError(ex, "Could not show the MCP consent window");
                answer.TrySetResult(McpConsentAnswer.Deny);
                return;
            }
            if (!services.GetRequiredService<ShellController>().IsMainWindowShown)
                notifications.Show($"{prompt.Client.Name} asks for your OK", $"{prompt.Request.Title}. Click to answer; no answer in 2 minutes means no.", Open);
        });
        logger.LogInformation("MCP {Client} asks: {Tool} ({Risk})", prompt.Client.Name, prompt.Request.Tool, prompt.Request.Risk);

        // Out of time, or the client left: close the question unanswered (the policy counts it as Deny).
        await using var registration = ct.Register(() => Dispatcher.UIThread.Post(() =>
        {
            answer.TrySetCanceled(ct);
            window?.Close();
        }));
        return await answer.Task.ConfigureAwait(false);
    }
}
