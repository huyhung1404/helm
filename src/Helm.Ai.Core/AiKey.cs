namespace Helm.Ai;

/// <summary>
/// One API key, synced (end-to-end encrypted) to every device, so the phone can call the same models. The secret is
/// never shown again after it is added: only <see cref="AiKeyStore.Mask"/>.
/// </summary>
public sealed record AiKey
{
    /// <summary><see cref="AiProvider.Id"/>.</summary>
    public required string Provider { get; init; }

    /// <summary>The user's name for it, e.g. the Console workspace it bills to ("Helm").</summary>
    public required string Label { get; init; }

    public required string Secret { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>The key a tool uses when it does not name one. At most one per provider is meant to have it.</summary>
    public bool IsDefault { get; init; }

    public AiKeyCheck? LastCheck { get; init; }
}

public enum AiKeyStatus
{
    /// <summary>The provider accepted the key and answered.</summary>
    Works,

    /// <summary>The key is valid but cannot be used now (no credit left, blocked by its workspace).</summary>
    Blocked,

    /// <summary>The provider rejected the key (wrong, deleted, disabled).</summary>
    Rejected,

    /// <summary>No answer about the key itself (no internet, the provider overloaded).</summary>
    Unknown,
}

/// <summary>The result of the last test of a key: what the provider said, and the models it offers the key.</summary>
public sealed record AiKeyCheck(DateTimeOffset At, AiKeyStatus Status, string Message, IReadOnlyList<AiModel> Models)
{
    /// <summary>The key's name in the provider's console, where the provider tells it (xAI).</summary>
    public string? RemoteName { get; init; }

    /// <summary>The account the key bills to, where the provider tells it (xAI: the team id the balance is read for).</summary>
    public string? Account { get; init; }
}

public sealed record AiModel(string Id, string Name);
