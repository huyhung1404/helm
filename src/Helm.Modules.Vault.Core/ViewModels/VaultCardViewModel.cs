using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>How an item is shown: the two fixed credential types, and Info for everything else.</summary>
public enum VaultItemType
{
    Login,
    Token,
    /// <summary>Anything with fields you add yourself (notes, cards, identities, documents, pictures).</summary>
    Info,
}

/// <summary>A kind of item with the name people see.</summary>
public sealed record VaultKindOption(VaultItemKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The names of the kinds of items, in the order the editor offers them.</summary>
public static class VaultKinds
{
    public static IReadOnlyList<VaultKindOption> Options { get; } =
    [
        new(VaultItemKind.Login, "Login"),
        new(VaultItemKind.Token, "Token"),
        new(VaultItemKind.Note, "Note"),
        new(VaultItemKind.Card, "Card"),
        new(VaultItemKind.Identity, "Identity"),
        new(VaultItemKind.Document, "Document"),
    ];

    public static string Label(VaultItemKind kind) => Options.FirstOrDefault(o => o.Kind == kind)?.Label ?? kind.ToString();
}

/// <summary>
/// One card of the list, closed: a login shows its icon, title, username and password (each with Copy); a token its
/// title and token (Copy); an Info item its title and description. Secrets stay hidden until revealed and are copied
/// through the protected clipboard.
/// </summary>
public sealed partial class VaultCardViewModel(VaultEntry entry, IVaultPlatform platform, VaultSession session) : ObservableObject
{
    private readonly VaultItem _item = entry.Item;

    [ObservableProperty] private bool _isRevealed;
    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private bool _isUsernameCopied;
    [ObservableProperty] private string _totpCode = "";
    [ObservableProperty] private int _totpSecondsLeft;
    [ObservableProperty] private bool _isTotpCopied;
    private readonly Totp? _totp = entry.Trashed ? null : entry.Item.Totp;

    public string Uid { get; } = entry.Uid;

    public VaultItemKind Kind => _item.Kind;

    public VaultItemType Type => TypeOf(_item.Kind);

    public static VaultItemType TypeOf(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => VaultItemType.Login,
        VaultItemKind.Token => VaultItemType.Token,
        _ => VaultItemType.Info,
    };

    public bool IsLogin => Type == VaultItemType.Login;

    public bool IsToken => Type == VaultItemType.Token;

    public bool IsInfo => Type == VaultItemType.Info;

    public string Title => _item.Title.Length > 0 ? _item.Title : "Untitled";

    public bool Trashed => entry.Trashed;

    public bool HasConflict => entry.Conflicts.Count > 0;

    public bool Favorite => _item.Favorite;

    public int Documents => _item.Attachments.Count;

    public string? Username => _item.Username;

    public bool HasUsername => Username is not null && !Trashed;

    /// <summary>The password, or the token of a token item.</summary>
    public bool HasSecret => _item.PrimarySecret is { Length: > 0 };

    /// <summary>Copy and reveal are offered on live items only; the trash is for restoring.</summary>
    public bool CanCopySecret => HasSecret && !Trashed;

    public string SecretDisplay => !HasSecret ? "" : IsRevealed ? _item.PrimarySecret! : "••••••••••••";

    public string CopyLabel => IsToken ? "Copy the token" : "Copy the password";

    /// <summary>An Info item's description: the start of its notes (the card shows at most two lines).</summary>
    public string Description => _item.Notes.Trim() is { Length: > 0 } notes ? (notes.Length > 240 ? notes[..240] + "…" : notes) : "";

    public bool HasDescription => IsInfo && Description.Length > 0;

    public string KindLabel => VaultKinds.Label(_item.Kind);

    /// <summary>
    /// The line under the title in the list: the username, else the first plain field (a cardholder, a name), else the
    /// start of an Info item's description, else the kind. Never a secret.
    /// </summary>
    public string Subtitle =>
        Username
        ?? _item.Fields.FirstOrDefault(f => !f.IsSecret && !f.IsFile && f.Kind is not (VaultFieldKind.Url or VaultFieldKind.Multiline) && f.Value.Trim().Length > 0)?.Value.Trim()
        ?? (IsInfo && Description.Length > 0 ? Description.Split('\n')[0].Trim() : null)
        ?? KindLabel;

    /// <summary>How much of the current code's time is left, 0 to 100 (the ring next to it, like the detail's fields).</summary>
    [ObservableProperty] private double _totpProgress;

    /// <summary>The last seconds of a code: wait for the next one before typing it.</summary>
    public bool TotpEndingSoon => HasTotp && TotpSecondsLeft <= 5;

    partial void OnTotpSecondsLeftChanged(int value) => OnPropertyChanged(nameof(TotpEndingSoon));

    public byte[]? Icon => _item.Icon;

    public bool HasIcon => _item.Icon is { Length: > 0 };

    public string Initial => VaultAvatar.Initial(_item.Title);

    /// <summary>"#RRGGBB", the same for the same name on every device.</summary>
    public string AvatarColor => VaultAvatar.Color(_item.Title);

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(SecretDisplay));

    /// <summary>The item has a two-factor secret: the card shows its current code with Copy.</summary>
    public bool HasTotp => _totp is not null;

    /// <summary>Called every second while the list is shown.</summary>
    internal void Tick(DateTimeOffset now)
    {
        if (_totp is null) return;
        TotpCode = Totp.Group(_totp.Code(now));
        TotpSecondsLeft = _totp.SecondsLeft(now);
        TotpProgress = 100.0 * TotpSecondsLeft / _totp.Period;
    }

    private int _totpCopies;

    [RelayCommand]
    private void CopyTotp()
    {
        if (_totp is null) return;
        platform.CopySecret(_totp.Code(DateTimeOffset.UtcNow));
        session.Touch();
        IsTotpCopied = true;
        _ = ClearCopiedAsync(++_totpCopies, () => _totpCopies, () => IsTotpCopied = false);
    }

    [RelayCommand]
    private void ToggleReveal()
    {
        IsRevealed = !IsRevealed;
        session.Touch();
    }

    private int _copies;
    private int _usernameCopies;

    /// <summary>Copies the password (or token) and shows "Copied" for 2 s. Not an async command: the button stays enabled meanwhile.</summary>
    [RelayCommand]
    private void Copy()
    {
        if (Trashed || _item.PrimarySecret is not { Length: > 0 } secret) return;
        platform.CopySecret(secret);
        session.Touch();
        IsCopied = true;
        _ = ClearCopiedAsync(++_copies, () => _copies, () => IsCopied = false);
    }

    [RelayCommand]
    private void CopyUsername()
    {
        if (Trashed || Username is not { } username) return;
        platform.CopyText(username);
        session.Touch();
        IsUsernameCopied = true;
        _ = ClearCopiedAsync(++_usernameCopies, () => _usernameCopies, () => IsUsernameCopied = false);
    }

    private static async Task ClearCopiedAsync(int copy, Func<int> latest, Action clear)
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        // A second copy meanwhile keeps its own full 2 s.
        if (copy == latest()) clear();
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
