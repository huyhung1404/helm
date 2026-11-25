using Helm.Core.Capture;
using Helm.Shell.Home;

namespace Helm.Home;

/// <summary>
/// What Home's capture box does with a line: "/t buy milk" goes to the target whose prefix is "t", anything else to the
/// default one (a note), among the targets of the tools that are on.
/// </summary>
public sealed class HomeCaptureHost(IEnumerable<ICaptureTarget> targets, ShellTools tools, IHomeLinks links, string defaultTarget = "note") : IHomeHost
{
    public Task<string> CaptureAsync(string text)
    {
        var enabled = tools.All.Where(t => t.IsEnabled).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var available = CaptureRouter.Available(targets, enabled.Contains);
        if (available.Count == 0) return Task.FromResult("Turn on Quick Capture's tools to capture here.");

        var fallback = available.FirstOrDefault(t => t.Id == defaultTarget) ?? available[0];
        var (target, body) = CaptureRouter.Resolve(available, fallback, text);
        if (target is null || body.Trim().Length == 0) return Task.FromResult("Nothing to add.");

        var result = target.Capture(body);
        return Task.FromResult(result.Message);
    }

    public void OpenWhatsNew() => links.OpenWhatsNew();
}
