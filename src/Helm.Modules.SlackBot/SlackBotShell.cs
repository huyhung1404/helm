using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

/// <summary>
/// The Windows app around Slack Report's view models: its page comes up through the shell, and the list on screen is
/// <see cref="SlackBotOverlay"/>. Both are resolved when used (the overlay itself depends on the inbox view model).
/// </summary>
public sealed class SlackBotShell(IServiceProvider services) : ISlackBotShell
{
    public void ShowPage() => services.GetRequiredService<IShellNavigation>().ShowPage(typeof(SlackBotContentPage));

    public void ResetOverlay() => services.GetRequiredService<SlackBotOverlay>().Reset();
}
