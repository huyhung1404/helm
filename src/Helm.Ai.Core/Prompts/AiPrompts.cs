using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Prompts;

/// <summary>
/// Each AI feature's prompt: Helm's built-in text, or the user's own version of it, kept as a saved prompt on their
/// Skill Hub (named <c>helm-&lt;feature id&gt;</c>) so it can be changed on the web without a new Helm.
/// </summary>
public interface IAiPrompts
{
    /// <summary>
    /// The prompt a call of <paramref name="featureId"/> (an <see cref="AiFeatureIds"/> value) uses: the Skill Hub's when
    /// one was fetched lately and it keeps every placeholder of <paramref name="builtIn"/>, else <paramref name="builtIn"/>.
    /// Pass the template before its placeholders (<c>{name}</c>) are filled in.
    /// </summary>
    string Get(string featureId, string builtIn);
}

/// <summary>One feature's prompt as the Skill Hub last gave it, on Helm Sync (the PC fetches it, the phone uses it too).</summary>
public sealed record AiPromptOverride
{
    /// <summary><see cref="AiFeatureIds"/>.</summary>
    public required string Feature { get; init; }

    /// <summary>The saved prompt's name on the Skill Hub (<see cref="AiPrompts.NameOf"/>).</summary>
    public required string Name { get; init; }

    public required string Text { get; init; }

    /// <summary>When the Skill Hub last said this is the prompt's text; too long ago and the built-in text is used.</summary>
    public DateTimeOffset CheckedAt { get; init; }
}

/// <summary>
/// The overrides fetched from the Skill Hub (collection <see cref="Collection"/>), and the rules for using one: fresh
/// (checked within <see cref="MaxAge"/>), not empty, at most <see cref="MaxLength"/> characters, and keeping the
/// built-in text's placeholders. Any other case is the built-in text; nothing here throws.
/// </summary>
public sealed partial class AiPrompts : IAiPrompts
{
    public const string Collection = "ai.prompts";

    /// <summary>A Skill Hub prompt overrides the feature whose id follows this ("helm-ask-helm" → ask-helm).</summary>
    public const string NamePrefix = "helm-";

    /// <summary>Longer than this is a mistake (a pasted document), not a prompt: ignored.</summary>
    internal const int MaxLength = 32_000;

    /// <summary>An override the Skill Hub has not confirmed for this long (the PC off, the hub unreachable) stops applying.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    /// <summary>The same text is written again (to refresh <see cref="AiPromptOverride.CheckedAt"/>) at most this often: each write syncs.</summary>
    internal static readonly TimeSpan Reconfirm = TimeSpan.FromHours(6);

    private readonly ISyncedCollection<AiPromptOverride> _cache;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>The text last warned about per feature, so a broken override is logged once, not on every call.</summary>
    private readonly ConcurrentDictionary<string, string> _warned = new(StringComparer.Ordinal);

    public AiPrompts(ISyncedCollection<AiPromptOverride> cache, ILogger? logger = null, TimeProvider? time = null)
    {
        _cache = cache;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public static string NameOf(string featureId) => NamePrefix + featureId;

    public string Get(string featureId, string builtIn)
    {
        if (_cache.Get(featureId) is not { } found) return builtIn;
        if (_time.GetUtcNow() - found.CheckedAt > MaxAge) return builtIn;
        if (Problem(found.Text, builtIn) is { } problem)
        {
            if (!_warned.TryGetValue(featureId, out var last) || last != found.Text)
            {
                _warned[featureId] = found.Text;
                _logger.LogWarning("Skill Hub prompt {Name} is not used: {Problem}; Helm's built-in prompt is", found.Name, problem);
            }
            return builtIn;
        }
        return found.Text;
    }

    /// <summary>
    /// Keeps what the Skill Hub said about <paramref name="featureId"/>'s prompt: its text, or null when it has none (no
    /// such prompt, or it was archived), which puts the feature back on the built-in text on every device.
    /// </summary>
    internal void Save(string featureId, string? text)
    {
        var name = NameOf(featureId);
        var existing = _cache.Get(featureId);
        if (text is not null && text.Length > MaxLength)
        {
            _logger.LogWarning("Skill Hub prompt {Name} is not used: {Length} characters, more than {Max}", name, text.Length, MaxLength);
            text = null;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            if (existing is not null && _cache.Delete(featureId))
                _logger.LogInformation("Skill Hub prompt {Name} is gone: {Feature} uses Helm's built-in prompt again", name, featureId);
            return;
        }
        var now = _time.GetUtcNow();
        if (existing is not null && existing.Text == text && now - existing.CheckedAt < Reconfirm) return;
        _cache.Upsert(featureId, new AiPromptOverride { Feature = featureId, Name = name, Text = text, CheckedAt = now });
        if (existing?.Text != text)
            _logger.LogInformation("Skill Hub prompt {Name} fetched ({Length} characters): {Feature} uses it", name, text.Length, featureId);
    }

    /// <summary>Why <paramref name="text"/> cannot stand in for <paramref name="builtIn"/>, or null when it can.</summary>
    internal static string? Problem(string text, string builtIn)
    {
        if (string.IsNullOrWhiteSpace(text)) return "it is empty";
        if (text.Length > MaxLength) return $"more than {MaxLength} characters";
        var missing = Placeholders(builtIn).Except(Placeholders(text), StringComparer.Ordinal).ToList();
        return missing.Count > 0 ? $"it lacks the placeholder{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing)}" : null;
    }

    /// <summary>The placeholders in a template: <c>{name}</c>, a letter first (JSON braces and <c>{{</c> are not).</summary>
    internal static IReadOnlyList<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"(?<!\{)\{[A-Za-z][A-Za-z0-9_]*\}(?!\})")]
    private static partial Regex PlaceholderPattern();
}
