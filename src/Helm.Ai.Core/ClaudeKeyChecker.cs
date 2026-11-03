using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Helm.Ai;

/// <summary>Tests a key: does the provider accept it, can it still spend, and which models does it offer.</summary>
public interface IAiKeyChecker
{
    AiProvider Provider { get; }

    Task<AiKeyCheck> CheckAsync(string secret, CancellationToken ct);
}

/// <summary>
/// Lists the models the key may use (free), then sends the smallest possible request to the cheapest model (a few
/// tokens, far below a cent): listing works without credit, so only a real request tells an empty balance apart.
/// </summary>
public sealed class ClaudeKeyChecker(TimeProvider? time = null) : IAiKeyChecker
{
    /// <summary>The cheapest current model, for the test request.</summary>
    internal const string TestModel = "claude-haiku-5-5";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AiProvider Provider => AiProviders.Claude;

    public async Task<AiKeyCheck> CheckAsync(string secret, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = secret.Trim(), MaxRetries = 1, Timeout = TimeSpan.FromSeconds(30) };
        List<AiModel> models = [];
        try
        {
            var page = await client.Models.List(cancellationToken: ct);
            for (var pages = 0; ; pages++)
            {
                models.AddRange(page.Items.Select(m => new AiModel(m.ID, m.DisplayName)));
                if (!page.HasNext() || pages >= 4) break;
                page = await page.Next(ct);
            }

            await client.Messages.Create(new MessageCreateParams
            {
                Model = TestModel,
                MaxTokens = 16,
                Messages = [new UserMessageParam { Content = "Reply with OK." }],
            }, ct);
            return Result(AiKeyStatus.Works, models.Count == 1 ? "Works. 1 model." : $"Works. {models.Count} models.");
        }
        catch (AnthropicUnauthorizedException)
        {
            return Result(AiKeyStatus.Rejected, "Claude does not accept this key: it is mistyped, or it was deleted or disabled in the Console.");
        }
        catch (AnthropicForbiddenException)
        {
            return Result(AiKeyStatus.Blocked, "The key is valid, but its workspace or organization does not allow this request.");
        }
        catch (AnthropicRateLimitException)
        {
            return Result(AiKeyStatus.Unknown, "Too many requests for this key right now. Try again in a minute.");
        }
        catch (Anthropic5xxException)
        {
            return Result(AiKeyStatus.Unknown, "Claude's servers had a problem. Try again later.");
        }
        catch (AnthropicBadRequestException ex) when (ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
        {
            return Result(AiKeyStatus.Blocked, "The key works, but the account has no credit left: add some under Billing in the Console.");
        }
        catch (AnthropicApiException ex)
        {
            return Result(models.Count > 0 ? AiKeyStatus.Blocked : AiKeyStatus.Unknown, $"Claude answered: {ex.Message}");
        }
        catch (AnthropicIOException)
        {
            return Result(AiKeyStatus.Unknown, "Could not reach Claude. Check the internet connection.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Result(AiKeyStatus.Unknown, "Could not reach Claude in time. Check the internet connection.");
        }

        AiKeyCheck Result(AiKeyStatus status, string message) => new(_time.GetUtcNow(), status, message, models);
    }
}
