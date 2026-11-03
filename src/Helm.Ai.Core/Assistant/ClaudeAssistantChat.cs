using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Helm.Ai.Assistant;

/// <summary>
/// Claude through the official Anthropic SDK: tools, adaptive thinking at the chosen effort, cached prefix, and the
/// server-side web search when a turn needs it (<see cref="WebSearch"/>).
/// </summary>
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

/// <summary>
/// Claude's web search tool for a model: the version with dynamic filtering on the models that take it, the basic one
/// on the others (Haiku). Results come localized to the user's time zone.
/// </summary>
internal static class ClaudeWebSearch
{
    /// <summary>The models that take <c>web_search_20260209</c> (dynamic filtering); the others get <c>web_search_20250305</c>.</summary>
    private static readonly HashSet<string> Filtering = new(StringComparer.Ordinal)
    {
        "claude-fable-5-1", "claude-fable-5", "claude-opus-5-5", "claude-opus-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6",
        "claude-sonnet-5-5", "claude-sonnet-5", "claude-sonnet-4-6",
    };

    public static ToolUnion Tool(string model, int maxUses) => Filtering.Contains(model)
        ? new ToolUnion(new WebSearchTool20260209 { MaxUses = maxUses, UserLocation = Location() })
        : new ToolUnion(new WebSearchTool20250305 { MaxUses = maxUses, UserLocation = Location() });

    /// <summary>The user's time zone (an IANA id, as the API takes it), and Vietnam when that is where it is.</summary>
    private static UserLocation? Location()
    {
        var zone = TimeZoneInfo.Local.Id;
        if (!zone.Contains('/') && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana)) zone = iana;
        if (!zone.Contains('/')) return null;
        return zone is "Asia/Ho_Chi_Minh" or "Asia/Saigon"
            ? new UserLocation { Timezone = zone, Country = "VN" }
            : new UserLocation { Timezone = zone };
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

    /// <summary>The web search tool is in <see cref="_tools"/>.</summary>
    private bool _web;

    /// <summary>How often a paused search turn (<c>pause_turn</c>) is sent again to go on.</summary>
    private const int MaxContinuations = 3;

    /// <summary>
    /// The instructions with a cache breakpoint of their own after the tools (they render first): another card with the
    /// same tools within 5 minutes reads both from the cache, and the conversation's own breakpoint (the top-level one)
    /// moves with it. A prefix under the model's minimum (512 tokens on the 5.x models, 4096 on Haiku 4.5) is simply
    /// not cached: no write premium is paid for it. A text call without instructions sends them as they are (no empty block).
    /// </summary>
    private readonly MessageCreateParamsSystem _system = string.IsNullOrWhiteSpace(system)
        ? system
        : new List<TextBlockParam> { new() { Text = system, CacheControl = new CacheControlEphemeral() } };

    /// <summary>The conversation as lines, to start it again from a short recap (<see cref="ChatRecap"/>).</summary>
    private readonly ChatRecap _recap = new();

    /// <summary>
    /// The tools changed after Claude thought: Claude ties its thinking blocks to the tools they were written under and
    /// refuses them under others (on accounts it checks), so the next call starts again from a recap instead of the history.
    /// </summary>
    private bool _restart;

    /// <summary>The history holds a thinking block.</summary>
    private bool _thought;

    public void AddUser(string text)
    {
        text = Turn(text);
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
        text = Turn(text);
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

    public void AddTools(IReadOnlyList<AssistantTool> tools)
    {
        _tools.AddRange(tools.Select(ToTool));
        _restart |= tools.Count > 0 && _thought;
    }

    public bool AddWebSearch(int maxUses)
    {
        if (!_web)
        {
            _tools.Add(ClaudeWebSearch.Tool(model, maxUses));
            _restart |= _thought;
        }
        _web = true;
        return true;
    }

    /// <summary>
    /// The user's turn as it goes: after a recap of the conversation instead of its history when the tools changed or
    /// the history grew long (<see cref="ChatRecap.MaxHistory"/>). Only at a turn's start, never in the middle of a
    /// round of calls; what waits (results of calls already run) is in the recap.
    /// </summary>
    private string Turn(string text)
    {
        if (!_restart && !_recap.TooLong)
        {
            _recap.User(text);
            return text;
        }
        _restart = _thought = false;
        _messages.Clear();
        _results = null;
        return _recap.StartAgain(text);
    }

    public async Task<AssistantStep> NextAsync(CancellationToken ct)
    {
        // The tools changed in the middle of a round (web search turned on by a call): go on from a recap.
        if (_restart) _messages.Add(new UserMessageParam { Content = Turn("Go on from here.") });
        if (_results is { } pending)
        {
            _messages.Add(new UserMessageParam { Content = pending });
            _results = null;
        }
        var response = await CreateAsync(ct);
        _imagePending = false;
        var answer = Read(response);
        long input = response.Usage.InputTokens, output = response.Usage.OutputTokens;
        long cacheRead = response.Usage.CacheReadInputTokens ?? 0, cacheWrite = response.Usage.CacheCreationInputTokens ?? 0;
        var searches = Searches(response.Usage);
        // A long search turn pauses: sent back as it is, it goes on where it stopped.
        for (var more = 0; response.StopReason == "pause_turn" && more < MaxContinuations; more++)
        {
            response = await CreateAsync(ct);
            var next = Read(response);
            answer = (Join(answer.Text, next.Text), [.. answer.Calls, .. next.Calls], [.. answer.Sources, .. next.Sources]);
            input += response.Usage.InputTokens;
            output += response.Usage.OutputTokens;
            cacheRead += response.Usage.CacheReadInputTokens ?? 0;
            cacheWrite += response.Usage.CacheCreationInputTokens ?? 0;
            searches += Searches(response.Usage);
        }
        _recap.Answer(answer.Text, answer.Calls);

        return new AssistantStep(WithSources(answer.Text, answer.Sources), answer.Calls,
            new AssistantUsage(input, output, cacheRead, cacheWrite, searches),
            Refused: response.StopReason == "refusal");
    }

    private async Task<Message> CreateAsync(CancellationToken ct)
    {
        try
        {
            return await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 4096,
                System = _system,
                Tools = _tools,
                Messages = _messages,
                OutputConfig = new OutputConfig { Effort = ToEffort(effort) },
                // The conversation so far, for the next call of this turn (most turns that read take two calls).
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
        catch (AnthropicBadRequestException ex) when (_web && ex.Message.Contains("web search", StringComparison.OrdinalIgnoreCase))
        {
            throw new AssistantException("Web search is turned off for this Claude organization: an admin turns it on under Settings › Capabilities in the Console.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new AssistantException($"Claude answered: {ex.Message}", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new AssistantException("Could not reach Claude. Check the internet connection.", ex);
        }
    }

    /// <summary>
    /// One response: its text, its tool calls and the sites its text cites. The whole content goes back into the
    /// conversation as it came (thinking with its signature, searches with their encrypted results, citations): Claude
    /// rejects an edited one. After a search, only the text written after the last result is the answer.
    /// </summary>
    private (string Text, IReadOnlyList<ToolCall> Calls, IReadOnlyList<string> Sources) Read(Message response)
    {
        var texts = new List<string>();
        var sources = new List<string>();
        var calls = new List<ToolCall>();
        List<ContentBlockParam> echo = [];
        var searched = false;
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                var cited = text.RawData.TryGetValue("citations", out var citations) ? WebSearch.Urls(citations).ToList() : [];
                texts.Add(text.Text);
                sources.AddRange(cited);
                echo.Add(cited.Count > 0 ? TextBlockParam.FromRawUnchecked(text.RawData) : new TextBlockParam { Text = text.Text });
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                // Sent back unchanged, signature and all: Claude rejects edited thinking.
                echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                _thought = true;
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                _thought = true;
            }
            else if (block.TryPickToolUse(out ToolUseBlock? use))
            {
                echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                calls.Add(new ToolCall(use.ID, use.Name, JsonSerializer.SerializeToElement(use.Input)));
            }
            else if (Server(block) is { } server)
            {
                // A search Claude ran (and, with dynamic filtering, the code that sifted its results): the text so far
                // was Claude saying it would look; the answer follows.
                echo.Add(server);
                searched = true;
                texts.Clear();
                sources.Clear();
            }
        }
        if (echo.Count > 0) _messages.Add(new AssistantMessageParam { Content = echo });
        // Cited text comes in pieces (one block per citation): they make one answer.
        return (string.Join(searched || sources.Count > 0 ? "" : "\n", texts).Trim(), calls, sources);
    }

    /// <summary>A block a server tool wrote (a search, its results, the code that filtered them), to send back unchanged.</summary>
    private static ContentBlockParam? Server(ContentBlock block)
    {
        if (block.TryPickServerToolUse(out ServerToolUseBlock? use)) return ServerToolUseBlockParam.FromRawUnchecked(use.RawData);
        if (block.TryPickWebSearchToolResult(out WebSearchToolResultBlock? search)) return WebSearchToolResultBlockParam.FromRawUnchecked(search.RawData);
        if (block.TryPickWebFetchToolResult(out WebFetchToolResultBlock? fetch)) return WebFetchToolResultBlockParam.FromRawUnchecked(fetch.RawData);
        if (block.TryPickCodeExecutionToolResult(out CodeExecutionToolResultBlock? code)) return CodeExecutionToolResultBlockParam.FromRawUnchecked(code.RawData);
        if (block.TryPickBashCodeExecutionToolResult(out BashCodeExecutionToolResultBlock? bash))
            return BashCodeExecutionToolResultBlockParam.FromRawUnchecked(bash.RawData);
        if (block.TryPickTextEditorCodeExecutionToolResult(out TextEditorCodeExecutionToolResultBlock? editor))
            return TextEditorCodeExecutionToolResultBlockParam.FromRawUnchecked(editor.RawData);
        if (block.TryPickContainerUpload(out ContainerUploadBlock? upload)) return ContainerUploadBlockParam.FromRawUnchecked(upload.RawData);
        return null;
    }

    private static long Searches(Usage usage) => usage.ServerToolUse?.WebSearchRequests ?? 0;

    private static string Join(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : $"{first} {second}";

    /// <summary>The sites the answer cites, when it does not name them itself (the card shows the text alone).</summary>
    private static string WithSources(string text, IReadOnlyList<string> urls)
    {
        if (text.Length == 0 || urls.Count == 0) return text;
        var hosts = WebSearch.Hosts(urls);
        if (hosts.Length == 0) return text;
        // "vnexpress.net" is named when the answer says "VnExpress", or lists its sources itself.
        var named = text.Contains("Nguồn", StringComparison.OrdinalIgnoreCase)
            || hosts.Split(", ").Any(h => text.Contains(h.Split('.')[0], StringComparison.OrdinalIgnoreCase));
        return named ? text : $"{text} Nguồn: {hosts}.";
    }

    public void AddResults(IReadOnlyList<ToolOutcome> results)
    {
        _recap.Results(results);
        _results = [.. results.Select(r => (ContentBlockParam)new ToolResultBlockParam
        {
            ToolUseID = r.CallId,
            Content = r.Content,
            IsError = r.IsError,
        })];
    }

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
