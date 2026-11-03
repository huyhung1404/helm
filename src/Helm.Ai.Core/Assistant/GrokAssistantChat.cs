using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>Grok through xAI's chat completions API (function tools), with the shared HTTP client.</summary>
public sealed class GrokAssistantChatFactory(HttpClient http) : IAssistantChatFactory
{
    public AiProvider Provider => AiProviders.Grok;

    public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools) =>
        new GrokAssistantChat(http, apiKey, model, system, tools);
}

internal sealed class GrokAssistantChat : IAssistantChat
{
    private const string Url = "https://api.x.ai/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _model;
    private readonly JsonArray _messages = [];
    private readonly JsonArray _tools = [];

    public GrokAssistantChat(HttpClient http, string key, string model, string system, IReadOnlyList<AssistantTool> tools)
    {
        _http = http;
        _key = key;
        _model = model;
        _messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var tool in tools)
        {
            _tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Schema.DeepClone(),
                },
            });
        }
    }

    public void AddUser(string text) => _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = _messages.DeepClone(),
            ["max_tokens"] = 4096,
        };
        if (_tools.Count > 0)
        {
            body["tools"] = _tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        JsonNode? json;
        int status;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            json = text.Length > 0 && text[0] == '{' ? JsonNode.Parse(text) : null;
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantException("Could not reach xAI. Check the internet connection.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException("xAI did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw new AssistantException("xAI answered something Helm could not read.", ex);
        }

        if (status is 401 or 403) throw new AssistantException("xAI does not accept the API key, or the team has no credit left. Test it on the AI page.");
        if (status == 429) throw new AssistantException("Too many requests right now, or the team has no credit left.");
        if (status >= 500) throw new AssistantException("xAI's servers had a problem. Try again later.");
        if (status != 200 || json?["choices"]?[0]?["message"] is not JsonObject message)
            throw new AssistantException($"xAI answered {status}: {json?["error"]?.ToString() ?? "no answer"}");

        // The answer goes back as it came, so the next call sees the tool calls it answers.
        _messages.Add(message.DeepClone());
        var calls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                var id = call["id"]?.GetValue<string>() ?? "";
                var name = call["function"]?["name"]?.GetValue<string>() ?? "";
                var arguments = call["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                JsonElement input;
                try
                {
                    input = JsonDocument.Parse(arguments.Length == 0 ? "{}" : arguments).RootElement.Clone();
                }
                catch (JsonException)
                {
                    input = JsonDocument.Parse("{}").RootElement.Clone();
                }
                calls.Add(new ToolCall(id, name, input));
            }
        }
        var usage = json["usage"];
        long Read(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        var prompt = Read(usage?["prompt_tokens"]);
        var cached = Read(usage?["prompt_tokens_details"]?["cached_tokens"]);
        return new AssistantStep(message["content"]?.GetValue<string>()?.Trim() ?? "", calls,
            new AssistantUsage(Math.Max(0, prompt - cached), Read(usage?["completion_tokens"]), cached, 0),
            Refused: false);
    }

    public void AddResults(IReadOnlyList<ToolOutcome> results)
    {
        foreach (var r in results)
            _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.Content });
    }
}
