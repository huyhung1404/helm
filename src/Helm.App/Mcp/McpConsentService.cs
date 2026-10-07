using System.Windows;
using Helm.App.Views;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.App.Mcp;

/// <summary>
/// Puts the consent policy's questions to the user (<see cref="McpConsentPolicy"/> decides when to ask). The dialog
/// opens on top of Helm; while Helm is hidden in the tray, a notification says Claude asks, and clicking it opens the
/// dialog. Nothing is ever allowed without a click on an Allow button.
/// </summary>
internal sealed class McpConsentService(IServiceProvider services, IUserNotifications notifications, ILogger<McpConsentService> logger) : IMcpConsentPrompt
{
    public async Task<McpConsentAnswer> AskAsync(McpConsentPrompt prompt, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<McpConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Application.Current.Dispatcher;
        McpConsentDialog? dialog = null;

        void Open()
        {
            if (answer.Task.IsCompleted || dialog is not null) return;
            var main = services.GetRequiredService<MainWindow>();
            // On top of everything: Claude's terminal usually has the focus, and Windows does not let a background app
            // take it, so a question that is not topmost could wait unseen behind it.
            dialog = new McpConsentDialog(prompt) { Topmost = true };
            if (main.IsVisible && main.WindowState != WindowState.Minimized)
            {
                dialog.Owner = main;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            dialog.Closed += (_, _) => answer.TrySetResult(dialog.Answer);
            dialog.Show();
            dialog.Activate();
        }

        await dispatcher.InvokeAsync(() =>
        {
            var main = services.GetRequiredService<MainWindow>();
            if (main.IsVisible) Open();
            else notifications.Show("Claude asks for your OK", $"{prompt.Request.Title}. Click to answer; no answer in 2 minutes means no.", Open);
        });
        logger.LogInformation("MCP {Client} asks: {Tool} ({Risk})", prompt.Client.Name, prompt.Request.Tool, prompt.Request.Risk);

        // Out of time, or the client left: close the question unanswered (the policy counts it as Deny).
        await using var registration = ct.Register(() => dispatcher.BeginInvoke(() =>
        {
            answer.TrySetCanceled(ct);
            dialog?.Close();
        }));
        return await answer.Task.ConfigureAwait(false);
    }
}
