using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>
/// One row of the Logins &amp; tokens list: icon (or a letter), app name, username, the password or token hidden until
/// revealed, and a copy button that says "Copied" for a moment. Copying goes through the protected clipboard.
/// </summary>
public sealed partial class PasswordRowViewModel(string uid, VaultItem item, IVaultPlatform platform, VaultSession session) : ObservableObject
{
    [ObservableProperty] private bool _isRevealed;
    [ObservableProperty] private bool _isCopied;

    public string Uid { get; } = uid;

    public string Title => item.Title.Length > 0 ? item.Title : "Untitled";

    public string Subtitle => item.Username ?? Website ?? (IsToken ? "Token" : "");

    public string? Website => item.Fields.FirstOrDefault(f => f.Kind == VaultFieldKind.Url && f.Value.Length > 0)?.Value;

    public bool IsToken => item.Kind == VaultItemKind.Token;

    /// <summary>The password, or the token of a token item.</summary>
    public bool HasSecret => item.PrimarySecret is { Length: > 0 };

    /// <summary>Hidden dots, the revealed value, or nothing (the row then shows no secret line at all).</summary>
    public string SecretDisplay => !HasSecret ? "" : IsRevealed ? item.PrimarySecret! : "••••••••••••";

    public string CopyLabel => IsToken ? "Copy the token" : "Copy the password";

    public byte[]? Icon => item.Icon;

    public bool HasIcon => item.Icon is { Length: > 0 };

    public string Initial => VaultAvatar.Initial(item.Title);

    /// <summary>"#RRGGBB", the same for the same name on every device.</summary>
    public string AvatarColor => VaultAvatar.Color(item.Title);

    public bool Favorite => item.Favorite;

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(SecretDisplay));

    [RelayCommand]
    private void ToggleReveal()
    {
        IsRevealed = !IsRevealed;
        session.Touch();
    }

    private int _copies;

    /// <summary>Copies the password (or token) and shows "Copied" for 2 s. Not an async command: the button stays enabled meanwhile.</summary>
    [RelayCommand]
    private void Copy()
    {
        if (item.PrimarySecret is not { Length: > 0 } secret) return;
        platform.CopySecret(secret);
        session.Touch();
        IsCopied = true;
        _ = ClearCopiedAsync(++_copies);
    }

    private async Task ClearCopiedAsync(int copy)
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        // A second copy meanwhile keeps its own full 2 s.
        if (copy == _copies) IsCopied = false;
    }

    [RelayCommand]
    private void CopyUsername()
    {
        if (item.Username is not { } username) return;
        platform.CopyText(username);
        session.Touch();
    }

    public override string ToString() => Title;
}

/// <summary>A letter on a colored circle for items without a picture.</summary>
public static class VaultAvatar
{
    // Fluent-ish, readable with white text in light and dark themes.
    private static readonly string[] Palette = ["#0F6CBD", "#C239B3", "#E3008C", "#CA5010", "#498205", "#038387", "#8764B8", "#4F6BED", "#B4009E", "#00727A"];

    public static string Initial(string title)
    {
        var letter = title.Trim().FirstOrDefault(char.IsLetterOrDigit);
        return letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
    }

    public static string Color(string title)
    {
        // FNV-1a: stable across processes (string.GetHashCode is not) and well spread over short names.
        var hash = 2166136261u;
        foreach (var c in title.Trim().ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return Palette[hash % (uint)Palette.Length];
    }
}
