using System.Text;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Sync;

/// <summary>
/// Tells a Healthchecks check (<c>$HELM_HC_URL</c>, e.g. https://hc-ping.com/&lt;uuid&gt;) that something needs the
/// user: <c>&lt;url&gt;/fail</c> with the reason as the body, which Healthchecks shows and alerts on. Nothing without a
/// URL; a ping that fails is only logged.
/// </summary>
internal sealed class HealthPing(string? url, HttpClient http, ILogger logger)
{
    public const string Variable = "HELM_HC_URL";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static HealthPing From(Func<string, string?> environment, ILogger logger) =>
        new(environment(Variable) is { Length: > 0 } url ? url : null, Shared, logger);

    public bool Enabled => url is not null;

    public async Task FailAsync(string reason)
    {
        if (url is null) return;
        try
        {
            using var body = new StringContent(reason, Encoding.UTF8, "text/plain");
            using var response = await http.PostAsync(url.TrimEnd('/') + "/fail", body).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) logger.LogWarning("The health check answered {Status} to a failure ping", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The health check could not be pinged");
        }
    }
}
