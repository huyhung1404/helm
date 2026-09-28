using System.Text.Json.Serialization;
using Helm.Core.Sync;

namespace Helm.Modules.Vault.Items;

public enum VaultItemKind
{
    Login,
    Note,
    Card,
    Identity,
    Document,
}

public enum VaultFieldKind
{
    Text,
    /// <summary>Hidden until revealed, copied with the clipboard protections (passwords, PINs, CVV).</summary>
    Secret,
    Username,
    Password,
    Url,
    Email,
    Phone,
    Number,
    Date,
    Multiline,
}

public sealed record VaultField(string Name, string Value, VaultFieldKind Kind = VaultFieldKind.Text)
{
    [JsonIgnore]
    public bool IsSecret => Kind is VaultFieldKind.Secret or VaultFieldKind.Password;

    /// <summary>Never prints the value: records land in logs and debugger views otherwise.</summary>
    public override string ToString() => $"VaultField({Name}, {Kind}, ***)";
}

/// <summary>A document attached to an item. <see cref="Blob"/> carries the file's own key; only the vault key reaches it.</summary>
public sealed record VaultAttachment(string Id, string Name, string MediaType, long Size, BlobRef Blob)
{
    public override string ToString() => $"VaultAttachment({Id}, {Size} bytes)";
}

/// <summary>An earlier version of an item, kept in the item itself (the last <see cref="VaultItem.MaxHistory"/>).</summary>
public sealed record VaultItemVersion(long SavedAtMs, VaultItem Item);

/// <summary>
/// The plaintext of one vault item: what is sealed with the vault key inside a <see cref="VaultItemRecord"/>. Only
/// exists in memory while the vault is unlocked.
/// </summary>
public sealed record VaultItem
{
    public const int MaxHistory = 10;

    public VaultItemKind Kind { get; init; }

    public string Title { get; init; } = "";

    public IReadOnlyList<VaultField> Fields { get; init; } = [];

    public string Notes { get; init; } = "";

    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool Favorite { get; init; }

    /// <summary>
    /// Optional picture the user chose for the item (PNG/JPEG/WebP bytes, at most <see cref="MaxIconBytes"/>). Sealed
    /// with the item like everything else; Helm never fetches icons from the web, which would reveal the user's sites.
    /// </summary>
    public byte[]? Icon { get; init; }

    public const int MaxIconBytes = 256 * 1024;

    public IReadOnlyList<VaultAttachment> Attachments { get; init; } = [];

    public long CreatedAtMs { get; init; }

    public long ModifiedAtMs { get; init; }

    /// <summary>Newest first. Entries have an empty history of their own.</summary>
    public IReadOnlyList<VaultItemVersion> History { get; init; } = [];

    /// <summary>Every blob this item keeps alive: its attachments and those of its history (docs/vault-design.md, I5).</summary>
    public IEnumerable<BlobRef> AllBlobs() =>
        Attachments.Select(a => a.Blob).Concat(History.SelectMany(h => h.Item.Attachments.Select(a => a.Blob)))
            .DistinctBy(b => b.Id);

    /// <summary>Same content, ignoring times and history (an edit that changes nothing is not a new version).</summary>
    public bool SameContentAs(VaultItem other) =>
        Kind == other.Kind && Title == other.Title && Notes == other.Notes && Favorite == other.Favorite
        && Fields.SequenceEqual(other.Fields) && Tags.SequenceEqual(other.Tags) && Attachments.Select(a => a.Id).SequenceEqual(other.Attachments.Select(a => a.Id))
        && (Icon ?? []).AsSpan().SequenceEqual(other.Icon ?? []);

    /// <summary>The password of a login (or the first password field of any item); null when there is none.</summary>
    [JsonIgnore]
    public string? Password => Fields.FirstOrDefault(f => f.Kind == VaultFieldKind.Password)?.Value;

    /// <summary>The username (or email) shown under the title in lists.</summary>
    [JsonIgnore]
    public string? Username => Fields.FirstOrDefault(f => f.Kind is VaultFieldKind.Username or VaultFieldKind.Email && f.Value.Length > 0)?.Value;

    public override string ToString() => $"VaultItem({Kind}, {Fields.Count} fields, ***)";

    /// <summary>A blank item of the given kind with the fields people expect for it.</summary>
    public static VaultItem New(VaultItemKind kind, string title = "") => new()
    {
        Kind = kind,
        Title = title,
        Fields = kind switch
        {
            VaultItemKind.Login =>
            [
                new("Username", "", VaultFieldKind.Username),
                new("Password", "", VaultFieldKind.Password),
                new("Website", "", VaultFieldKind.Url),
            ],
            VaultItemKind.Card =>
            [
                new("Cardholder", ""),
                new("Number", "", VaultFieldKind.Secret),
                new("Expiry", "", VaultFieldKind.Date),
                new("CVV", "", VaultFieldKind.Secret),
                new("PIN", "", VaultFieldKind.Secret),
            ],
            VaultItemKind.Identity =>
            [
                new("Full name", ""),
                new("ID number", "", VaultFieldKind.Secret),
                new("Date of birth", "", VaultFieldKind.Date),
                new("Phone", "", VaultFieldKind.Phone),
                new("Email", "", VaultFieldKind.Email),
                new("Address", "", VaultFieldKind.Multiline),
            ],
            _ => [],
        },
    };
}
