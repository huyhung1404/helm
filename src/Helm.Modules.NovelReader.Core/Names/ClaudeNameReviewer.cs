using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Names;

/// <summary>A Claude model the AI name scan can use.</summary>
/// <param name="InputPrice">US dollars per million input tokens, when known (for the estimate before a scan).</param>
public sealed record ClaudeModel(string Id, string Name, double? InputPrice, double? OutputPrice);

/// <summary>The AI could not answer: no key, a refused key, no connection, the service busy.</summary>
public sealed class NameReviewException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a scan with Claude would send and cost, before it runs.</summary>
public sealed record NameReviewEstimate(int Candidates, int InputTokens, int OutputTokens, double? Dollars);

/// <summary>
/// The AI name scan: Claude (the user's own Anthropic API key) reads each candidate with a couple of sentences of the
/// novel where it appears and says whether it is a name — a person, a place, a group — and how a Vietnamese
/// QuickTranslator reader writes it. The candidates come from <see cref="NameScanner.Candidates"/> (wider than the logic
/// scan keeps), so Claude also confirms names without a surname and throws out words the logic took for names. Only the
/// candidates and their example sentences are sent, never the whole novel.
/// </summary>
public sealed class ClaudeNameReviewer(HttpClient http)
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";

    /// <summary>Candidates per request: a request stays small and quick, and a failure loses little.</summary>
    public const int BatchSize = 40;

    public static IReadOnlyList<ClaudeModel> Models { get; } =
    [
        new("claude-haiku-4-5-20251001", "Claude Haiku 4.5 (fast, cheapest)", 1, 5),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5 (more accurate)", null, null),
        new("claude-opus-5-5", "Claude Opus 5.5 (most accurate)", null, null),
    ];

    public static ClaudeModel DefaultModel => Models[0];

    /// <summary>Tokens of the instructions sent with every request.</summary>
    private const int InstructionTokens = 700;

    /// <summary>Tokens of the answer for one candidate.</summary>
    private const int AnswerTokens = 30;

    private const string Instructions =
        """
        You help a reader of Chinese web novels who reads them through a Vietnamese QuickTranslator (VietPhrase) converter.
        The converter knows common words but not this novel's names, so names come out as ordinary words. You get
        candidate words found in the novel, each with sentences where it appears. For each one, decide whether in this
        novel it is a proper name:
        - "person": a character's name, a given name, a courtesy name or a nickname used as a name (林宛, 景桓, 阿七);
        - "place": a named place, city, palace, mountain, bridge (万安桥);
        - "group": a named sect, clan, family house used as a name, organisation (林府, 洛家);
        - "title": a rank or form of address, not a name (侯爷, 皇上);
        - "not_name": anything else — a word cut out of a longer word or idiom (成怒 of 恼羞成怒), a name with a word
          stuck to it (向林宛, 谢珩低), an ordinary word or phrase.
        For names, give how a Vietnamese reader writes it: the Hán Việt reading, every syllable capitalised for a person or
        a place (林宛 → Lâm Uyển, 洛家 → Lạc gia: the family word stays lowercase). Judge from the sentences, not from the
        characters alone. Answer with the report_names tool, one entry per candidate, in the order given.
        """;

    /// <summary>What the request for <paramref name="candidates"/> would use, roughly.</summary>
    public static NameReviewEstimate Estimate(IReadOnlyList<NameFinding> candidates, ClaudeModel model)
    {
        var batches = (candidates.Count + BatchSize - 1) / BatchSize;
        // Chinese is about a token a character; the examples are most of it.
        var input = batches * InstructionTokens + candidates.Sum(c => c.Chinese.Length + c.Examples.Sum(e => e.Length) + 12);
        var output = candidates.Count * AnswerTokens;
        double? dollars = model.InputPrice is { } inPrice && model.OutputPrice is { } outPrice
            ? (input * inPrice + output * outPrice) / 1_000_000
            : null;
        return new NameReviewEstimate(candidates.Count, input, output, dollars);
    }

    /// <summary>Checks a key with the smallest request there is.</summary>
    /// <exception cref="NameReviewException">The key is refused or the service cannot be reached.</exception>
    public async Task CheckKeyAsync(string apiKey, ClaudeModel model, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["max_tokens"] = 1,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hi" }),
        };
        await SendAsync(apiKey, body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks Claude about every candidate, <see cref="BatchSize"/> at a time (two requests at once). Candidates Claude
    /// takes for names come back as sure findings with its reading and kind; the rest are left out.
    /// </summary>
    /// <exception cref="NameReviewException">Claude could not answer (the scan then falls back to the logic one).</exception>
    public async Task<IReadOnlyList<NameFinding>> ReviewAsync(IReadOnlyList<NameFinding> candidates, string apiKey, ClaudeModel model,
        IProgress<double>? progress, CancellationToken ct)
    {
        var batches = candidates.Chunk(BatchSize).ToList();
        var results = new IReadOnlyList<NameFinding>[batches.Count];
        var done = 0;
        using var slots = new SemaphoreSlim(2);
        await Task.WhenAll(batches.Select(async (batch, index) =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[index] = await ReviewBatchAsync(batch, apiKey, model, ct).ConfigureAwait(false);
                progress?.Report((double)Interlocked.Increment(ref done) / batches.Count);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    private async Task<IReadOnlyList<NameFinding>> ReviewBatchAsync(IReadOnlyList<NameFinding> batch, string apiKey, ClaudeModel model, CancellationToken ct)
    {
        var request = new StringBuilder("Candidates:\n");
        for (var i = 0; i < batch.Count; i++)
        {
            request.Append(i + 1).Append(". ").Append(batch[i].Chinese).Append('\n');
            foreach (var example in batch[i].Examples) request.Append("   - ").Append(example).Append('\n');
        }
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["max_tokens"] = Math.Max(1024, batch.Count * AnswerTokens * 2),
            ["system"] = Instructions,
            ["tools"] = new JsonArray(Tool()),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "report_names" },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.ToString() }),
        };
        var reply = await SendAsync(apiKey, body, ct).ConfigureAwait(false);
        return Parse(reply, batch);
    }

    /// <summary>Reads Claude's report: the candidates it calls a person, a place or a group, with its reading.</summary>
    internal static IReadOnlyList<NameFinding> Parse(JsonNode reply, IReadOnlyList<NameFinding> batch)
    {
        var byWord = batch.GroupBy(c => c.Chinese, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var names = new List<NameFinding>();
        foreach (var block in reply["content"]?.AsArray() ?? [])
        {
            if (block?["type"]?.GetValue<string>() != "tool_use") continue;
            foreach (var entry in block["input"]?["names"]?.AsArray() ?? [])
            {
                var word = entry?["word"]?.GetValue<string>() ?? "";
                var kind = entry?["kind"]?.GetValue<string>() ?? "not_name";
                var reading = (entry?["vietnamese"]?.GetValue<string>() ?? "").Trim();
                if (!byWord.TryGetValue(word, out var candidate) || kind is not ("person" or "place" or "group")) continue;
                names.Add(candidate with
                {
                    Vietnamese = reading.Length > 0 ? reading : candidate.Vietnamese,
                    Confidence = NameConfidence.Sure,
                    Kind = kind,
                });
            }
        }
        return names;
    }

    private static JsonObject Tool() => new()
    {
        ["name"] = "report_names",
        ["description"] = "Report, for every candidate, whether it is a name in this novel and how to write it in Vietnamese.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["names"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["word"] = new JsonObject { ["type"] = "string", ["description"] = "The candidate, exactly as given." },
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("person", "place", "group", "title", "not_name") },
                            ["vietnamese"] = new JsonObject { ["type"] = "string", ["description"] = "How a Vietnamese reader writes it; empty when it is not a name." },
                        },
                        ["required"] = new JsonArray("word", "kind", "vietnamese"),
                    },
                },
            },
            ["required"] = new JsonArray("names"),
        },
    };

    /// <summary>One request, tried again when the service is busy (429, 529, 5xx) after a short wait.</summary>
    private async Task<JsonNode> SendAsync(string apiKey, JsonObject body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new NameReviewException("There is no Anthropic API key. Add it in Novel Reader's settings.");
        var json = body.ToJsonString();
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-api-key", apiKey.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new NameReviewException("Claude cannot be reached. Check the connection.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new NameReviewException("Claude did not answer in time.", ex);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        return JsonNode.Parse(text) ?? throw new NameReviewException("Claude sent an empty answer.");
                    }
                    catch (JsonException ex)
                    {
                        throw new NameReviewException("Claude's answer could not be read.", ex);
                    }
                }
                var status = (int)response.StatusCode;
                if ((status is 429 or 529 or >= 500) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw new NameReviewException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "The Anthropic API key was refused. Check it in Novel Reader's settings.",
                    HttpStatusCode.Forbidden => "The Anthropic API key may not use this model.",
                    HttpStatusCode.TooManyRequests => "Claude is busy (rate limit). Try again in a minute.",
                    _ => $"Claude refused the request ({status}): {ErrorMessage(text)}",
                });
            }
        }
    }

    private static string ErrorMessage(string text)
    {
        try
        {
            return JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? "no details";
        }
        catch (JsonException)
        {
            return "no details";
        }
    }
}
