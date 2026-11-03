using System.Net.Http.Headers;
using System.Text.Json;

namespace Helm.Ai;

/// <summary>The one HTTP client the providers without an SDK here (xAI) are called with.</summary>
public static class AiHttp
{
    public static HttpClient Shared { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>GETs <paramref name="url"/> with a bearer key: the status, and the JSON body when there is one.</summary>
    internal static async Task<(int Status, JsonElement? Json)> GetAsync(HttpClient http, string url, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonElement? json = null;
        if (body.Length > 0 && body[0] is '{' or '[')
        {
            using var doc = JsonDocument.Parse(body);
            json = doc.RootElement.Clone();
        }
        return ((int)response.StatusCode, json);
    }

    internal static string? Text(this JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    internal static bool Flag(this JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Tests a Grok key, for free: xAI tells about the key itself (its name in the console, its team, whether it or its team
/// is blocked) and lists the models it may use.
/// </summary>
public sealed class GrokKeyChecker(HttpClient http, TimeProvider? time = null) : IAiKeyChecker
{
    internal const string BaseUrl = "https://api.x.ai/v1/";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AiProvider Provider => AiProviders.Grok;

    public async Task<AiKeyCheck> CheckAsync(string secret, CancellationToken ct)
    {
        secret = secret.Trim();
        List<AiModel> models = [];
        string? name = null, team = null;
        try
        {
            var (status, info) = await AiHttp.GetAsync(http, BaseUrl + "api-key", secret, ct);
            if (status is 400 or 401 or 403)
                return Result(AiKeyStatus.Rejected, "xAI does not accept this key: it is mistyped, or it was deleted in the xAI Console.");
            if (status == 429) return Result(AiKeyStatus.Unknown, "Too many requests for this key right now. Try again in a minute.");
            if (status >= 500) return Result(AiKeyStatus.Unknown, "xAI's servers had a problem. Try again later.");
            if (status != 200 || info is not { } key) return Result(AiKeyStatus.Unknown, $"xAI answered {status}.");
            name = key.Text("name");
            team = key.Text("team_id");

            var (listed, list) = await AiHttp.GetAsync(http, BaseUrl + "models", secret, ct);
            if (listed == 200 && list is { } l && l.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                models.AddRange(data.EnumerateArray().Select(m => m.Text("id")).OfType<string>().Select(id => new AiModel(id, id)));
            }

            if (key.Flag("api_key_disabled")) return Result(AiKeyStatus.Blocked, "This key is turned off in the xAI Console.");
            if (key.Flag("api_key_blocked")) return Result(AiKeyStatus.Blocked, "xAI has blocked this key.");
            if (key.Flag("team_blocked")) return Result(AiKeyStatus.Blocked, "xAI has blocked this key's team (often: no credit left, or a payment problem).");
            return Result(AiKeyStatus.Works, models.Count == 1 ? "Works. 1 model." : $"Works. {models.Count} models.");
        }
        catch (HttpRequestException)
        {
            return Result(AiKeyStatus.Unknown, "Could not reach xAI. Check the internet connection.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(AiKeyStatus.Unknown, "xAI did not answer in time. Check the internet connection.");
        }
        catch (JsonException)
        {
            return Result(AiKeyStatus.Unknown, "xAI answered something Helm could not read.");
        }

        AiKeyCheck Result(AiKeyStatus status, string message) =>
            new(_time.GetUtcNow(), status, message, models) { RemoteName = name, Account = team };
    }
}
