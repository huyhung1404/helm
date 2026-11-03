using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Prompts;

/// <summary>Where the users' own prompts come from (the Skill Hub); a double in tests.</summary>
public interface IAiPromptSource
{
    /// <summary>False until the source is set up on this device: then nothing is asked.</summary>
    bool IsSetUp { get; }

    /// <summary>The latest text of the saved prompt <paramref name="name"/>; null when there is none (or it is archived).</summary>
    /// <exception cref="AiPromptSourceException">The source could not be asked, or its answer could not be read.</exception>
    Task<string?> GetAsync(string name, CancellationToken ct);
}

/// <summary>The prompt source could not tell (no internet, a token refused): the prompts fetched before stay.</summary>
public sealed class AiPromptSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the user's saved prompts from their Skill Hub over its MCP endpoint (one JSON-RPC <c>prompts/get</c> per
/// prompt, each saved prompt being the MCP prompt <c>p-&lt;name&gt;</c>), with this machine's Skill Hub token and the
/// Cloudflare Access service token in front of it. Only reads: nothing is ever written to the hub.
/// </summary>
public sealed class SkillHubPromptSource(HttpClient http, Func<SkillHubConnection?> connection) : IAiPromptSource
{
    /// <summary>A saved prompt as an MCP prompt: this, then its name.</summary>
    internal const string McpPrefix = "p-";

    /// <summary>The JSON-RPC error the hub answers for a prompt it does not have (or that is archived).</summary>
    private const int UnknownPrompt = -32602;

    /// <summary>
    /// The client for it: no redirects (Cloudflare Access sends a refused request to its sign-in page) and a cap on the
    /// answer's size.
    /// </summary>
    public static HttpClient CreateClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1_000_000 };

    public bool IsSetUp => connection() is not null;

    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        if (connection() is not { } hub) throw new AiPromptSourceException("The Skill Hub is not set up on this device.");
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "prompts/get",
            ["params"] = new JsonObject { ["name"] = McpPrefix + name },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, hub.Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hub.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (hub.AccessClientId is { Length: > 0 } id && hub.AccessClientSecret is { Length: > 0 } secret)
        {
            request.Headers.Add("CF-Access-Client-Id", id);
            request.Headers.Add("CF-Access-Client-Secret", secret);
        }

        string text;
        HttpStatusCode status;
        try
        {
            using var response = await http.SendAsync(request, ct);
            status = response.StatusCode;
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AiPromptSourceException("Could not reach the Skill Hub.", ex);
        }

        if (status == HttpStatusCode.Unauthorized) throw new AiPromptSourceException("The Skill Hub does not accept this machine's token.");
        if (status is HttpStatusCode.Forbidden || (int)status is >= 300 and < 400)
            throw new AiPromptSourceException("Cloudflare Access turned the request away: check the service token.");
        if (status != HttpStatusCode.OK) throw new AiPromptSourceException($"The Skill Hub answered {(int)status}.");
        return Read(text);
    }

    /// <summary>The prompt's text from a <c>prompts/get</c> answer; null for "no such prompt".</summary>
    internal static string? Read(string answer)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(answer);
        }
        catch (JsonException ex)
        {
            throw new AiPromptSourceException("The Skill Hub answered something Helm could not read.", ex);
        }
        if (root is not JsonObject reply) throw new AiPromptSourceException("The Skill Hub answered something Helm could not read.");
        if (reply["error"] is JsonObject error)
        {
            if (error["code"] is JsonValue code && code.TryGetValue<int>(out var n) && n == UnknownPrompt) return null;
            throw new AiPromptSourceException("The Skill Hub could not give the prompt.");
        }
        if (reply["result"]?["messages"] is JsonArray { Count: > 0 } messages
            && messages[0]?["content"]?["text"] is JsonValue value && value.TryGetValue<string>(out var prompt))
            return prompt;
        throw new AiPromptSourceException("The Skill Hub's answer has no prompt text.");
    }
}
