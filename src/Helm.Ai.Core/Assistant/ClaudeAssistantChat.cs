using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Helm.Ai.Assistant;

/// <summary>Claude through the official Anthropic SDK: tools, adaptive thinking at the chosen effort, cached prefix.</summary>
public sealed class ClaudeAssistantChatFactory(HttpClient? http = null) : IAssistantChatFactory
{
    public AiProvider Provider => AiProviders.Claude;

    public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
    {
        var client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 1, Timeout = TimeSpan.FromSeconds(60) };
        if (http is not null) client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 0, HttpClient = http };
        return new ClaudeAssistantChat(client, model, effort, system, tools);
    }
}

internal sealed class ClaudeAssistantChat(AnthropicClient client, string model, string effort, string system, IReadOnlyList<AssistantTool> tools) : IAssistantChat
{
    private readonly List<MessageParam> _messages = [];
    private readonly List<ToolUnion> _tools = [.. tools.Select(ToTool)];

    /// <summary>Tool results not sent yet: a turn that ended on its changes sends them with the next turn's text.</summary>
    private List<ContentBlockParam>? _results;

    /// <summary>A photo went with the last user message and Claude has not answered it yet.</summary>
    private bool _imagePending;

    public void AddUser(string text)
    {
        if (_results is { } results)
        {
            List<ContentBlockParam> blocks = [.. results, new TextBlockParam { Text = text }];
            _messages.Add(new UserMessageParam { Content = blocks });
            _results = null;
        }
        else
        {
            _messages.Add(new UserMessageParam { Content = text });
        }
    }

    /// <summary>The photo first, then the words (Claude reads a picture best before the question about it).</summary>
    public void AddUser(string text, AssistantImage image)
    {
        List<ContentBlockParam> blocks =
        [
            .. _results ?? [],
            new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(image.Data), MediaType = ToMediaType(image.MediaType) } },
            new TextBlockParam { Text = text },
        ];
        _results = null;
        _messages.Add(new UserMessageParam { Content = blocks });
        _imagePending = true;
    }

    public void AddTools(IReadOnlyList<AssistantTool> tools) => _tools.AddRange(tools.Select(ToTool));

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        if (_results is { } pending)
        {
            _messages.Add(new UserMessageParam { Content = pending });
            _results = null;
        }
        Message response;
        try
        {
            response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 4096,
                System = system,
                Tools = _tools,
                Messages = _messages,
                OutputConfig = new OutputConfig { Effort = ToEffort(effort) },
                // The tools and the instructions stay the same: later calls read them from the cache.
                CacheControl = new CacheControlEphemeral(),
            }, ct);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new AssistantException("Claude does not accept the API key. Test it on the AI page.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new AssistantException("Too many requests right now. Try again in a minute.", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new AssistantException("Claude's servers had a problem. Try again later.", ex);
        }
        catch (AnthropicBadRequestException ex) when (_imagePending && ex.Message.Contains("image", StringComparison.OrdinalIgnoreCase))
        {
            throw new AssistantImageException($"{model} does not take this photo.", ex);
        }
        catch (AnthropicBadRequestException ex) when (ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
        {
            throw new AssistantException("The Claude account has no credit left: add some under Billing in the Console.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new AssistantException($"Claude answered: {ex.Message}", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new AssistantException("Could not reach Claude. Check the internet connection.", ex);
        }

        _imagePending = false;
        var texts = new List<string>();
        var calls = new List<ToolCall>();
        List<ContentBlockParam> echo = [];
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                texts.Add(text.Text);
                echo.Add(new TextBlockParam { Text = text.Text });
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                // Sent back unchanged, signature and all: Claude rejects edited thinking.
                echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? use))
            {
                echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                calls.Add(new ToolCall(use.ID, use.Name, JsonSerializer.SerializeToElement(use.Input)));
            }
        }
        if (echo.Count > 0) _messages.Add(new AssistantMessageParam { Content = echo });

        var usage = response.Usage;
        return new AssistantStep(string.Join("\n", texts).Trim(), calls,
            new AssistantUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0),
            Refused: response.StopReason == "refusal");
    }

    public void AddResults(IReadOnlyList<ToolOutcome> results) =>
        _results = [.. results.Select(r => (ContentBlockParam)new ToolResultBlockParam
        {
            ToolUseID = r.CallId,
            Content = r.Content,
            IsError = r.IsError,
        })];

    private static ToolUnion ToTool(AssistantTool tool)
    {
        var properties = new Dictionary<string, JsonElement>();
        if (tool.Schema["properties"] is System.Text.Json.Nodes.JsonObject props)
            foreach (var (name, value) in props) properties[name] = JsonSerializer.SerializeToElement(value);
        var required = tool.Schema["required"] is System.Text.Json.Nodes.JsonArray list
            ? list.Select(n => n!.GetValue<string>()).ToList()
            : [];
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static MediaType ToMediaType(string type) => type switch
    {
        "image/png" => MediaType.ImagePng,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImageJpeg,
    };

    private static Effort ToEffort(string effort) => effort switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => Effort.Low,
    };
}
