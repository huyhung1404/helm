using Helm.Core.Sync;

namespace Helm.Ai;

/// <summary>The user's API keys (collection <see cref="Collection"/>, on Helm Sync).</summary>
public sealed class AiKeyStore
{
    public const string Collection = "ai.keys";

    private readonly ISyncedCollection<AiKey> _keys;
    private readonly TimeProvider _time;

    public AiKeyStore(ISyncedCollection<AiKey> keys, TimeProvider? time = null)
    {
        _keys = keys;
        _time = time ?? TimeProvider.System;
        _keys.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after a local change and after another device's change arrives (then on a thread-pool thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Every key, oldest first.</summary>
    public IReadOnlyList<SyncedItem<AiKey>> All() =>
        [.. _keys.All().OrderBy(k => k.Value.AddedAt).ThenBy(k => k.Id, StringComparer.Ordinal)];

    public AiKey? Get(string id) => _keys.Get(id);

    /// <summary>
    /// The key a tool uses for <paramref name="provider"/>: the one marked default (the newest, should two devices
    /// each have marked one), else the oldest.
    /// </summary>
    public SyncedItem<AiKey>? DefaultFor(AiProvider provider)
    {
        var keys = All().Where(k => k.Value.Provider == provider.Id).ToList();
        return keys.Where(k => k.Value.IsDefault).OrderByDescending(k => k.UpdatedAt).FirstOrDefault() ?? keys.FirstOrDefault();
    }

    /// <summary>Why <paramref name="secret"/> cannot be a key of <paramref name="provider"/>, or null when it looks right.</summary>
    public static string? Problem(AiProvider provider, string secret)
    {
        secret = secret.Trim();
        if (secret.Length == 0) return "Paste the key.";
        if (secret.Any(char.IsWhiteSpace)) return "A key has no spaces or line breaks: copy it again.";
        if (!secret.StartsWith(provider.KeyPrefix, StringComparison.Ordinal))
            return $"A {provider.Name} key starts with {provider.KeyPrefix}";
        if (secret.Length < provider.KeyPrefix.Length + 20) return "This key is too short: copy all of it.";
        return null;
    }

    /// <summary>The key as the page shows it: its prefix and its last four characters ("sk-ant-…x7Qa", "xai-…b2Zz").</summary>
    public static string Mask(string secret)
    {
        if (secret.Length <= 12) return new string('•', secret.Length);
        var first = secret.IndexOf('-');
        var second = first < 0 ? -1 : secret.IndexOf('-', first + 1);
        var end = second is > 0 and < 10 ? second : first is > 0 and < 10 ? first : -1;
        var prefix = end > 0 ? secret[..(end + 1)] : secret[..3];
        return $"{prefix}…{secret[^4..]}";
    }

    /// <summary>True when a saved key already is this secret.</summary>
    public bool Contains(string secret)
    {
        var trimmed = secret.Trim();
        return _keys.All().Any(k => k.Value.Secret == trimmed);
    }

    /// <summary>Saves a new key; the first key of a provider becomes its default.</summary>
    /// <exception cref="ArgumentException">The secret does not look like a key of the provider (see <see cref="Problem"/>).</exception>
    public string Add(AiProvider provider, string label, string secret, AiKeyCheck? check = null)
    {
        secret = secret.Trim();
        if (Problem(provider, secret) is { } problem) throw new ArgumentException(problem, nameof(secret));
        return _keys.Add(new AiKey
        {
            Provider = provider.Id,
            Label = CleanLabel(label, provider),
            Secret = secret,
            AddedAt = _time.GetUtcNow(),
            IsDefault = DefaultFor(provider) is null,
            LastCheck = check,
        });
    }

    public void Rename(string id, string label)
    {
        if (_keys.Get(id) is { } key) _keys.Upsert(id, key with { Label = CleanLabel(label, AiProviders.Find(key.Provider)) });
    }

    public void SaveCheck(string id, AiKeyCheck check)
    {
        if (_keys.Get(id) is { } key) _keys.Upsert(id, key with { LastCheck = check });
    }

    /// <summary>Makes <paramref name="id"/> its provider's default and clears the flag on the provider's other keys.</summary>
    public void MakeDefault(string id)
    {
        if (_keys.Get(id) is not { } chosen) return;
        foreach (var other in _keys.All().Where(k => k.Id != id && k.Value.Provider == chosen.Provider && k.Value.IsDefault))
            _keys.Upsert(other.Id, other.Value with { IsDefault = false });
        if (!chosen.IsDefault) _keys.Upsert(id, chosen with { IsDefault = true });
    }

    /// <summary>Deletes the key on every device; when it was the default, the provider's oldest other key takes over.</summary>
    public bool Delete(string id)
    {
        var key = _keys.Get(id);
        if (key is null || !_keys.Delete(id)) return false;
        if (key.IsDefault && AiProviders.Find(key.Provider) is { } provider && DefaultFor(provider) is { } next)
            MakeDefault(next.Id);
        return true;
    }

    private static string CleanLabel(string label, AiProvider? provider)
    {
        var clean = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 40) clean = clean[..40].TrimEnd();
        return clean.Length > 0 ? clean : provider?.Name ?? "Key";
    }
}
