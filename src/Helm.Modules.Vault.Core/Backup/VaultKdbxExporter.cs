using System.Security.Cryptography;
using System.Text;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;

namespace Helm.Modules.Vault.Backup;

/// <param name="IncludeDocuments">Attach documents to the entries. The whole file is built in memory, so large vaults may leave this off.</param>
public sealed record KdbxExportOptions(string Password, bool IncludeDocuments = true, bool IncludeHistory = true, bool IncludeTrash = false);

/// <summary>
/// Exports the vault as a KeePass database (KDBX 4.1): the way out if Helm ever stops working. Passwords and secret
/// fields stay protected, one group per kind of item, documents as attachments, history as entry history.
/// </summary>
public sealed class VaultKdbxExporter(VaultStore store, VaultFiles files)
{
    /// <summary>An item to export, from the vault or from a backup snapshot (the restore tool).</summary>
    public sealed record Source(string Uid, VaultItem Item, bool Trashed);

    private static readonly HashSet<string> Standard = new(StringComparer.OrdinalIgnoreCase) { "Title", "UserName", "Password", "URL", "Notes" };

    public async Task<int> ExportAsync(Stream output, KdbxExportOptions options, CancellationToken ct = default) =>
        await ExportAsync(output, options, KdbxKdf.Default, ct).ConfigureAwait(false);

    internal Task<int> ExportAsync(Stream output, KdbxExportOptions options, KdbxKdf kdf, CancellationToken ct) =>
        ExportItemsAsync(output, options,
            [.. store.Items().Select(e => new Source(e.Uid, e.Item, false)), .. store.Trash().Select(e => new Source(e.Uid, e.Item, true))],
            (attachment, token) => files.ReadAllAsync(attachment, token), kdf, ct);

    /// <summary>Exports any items; attachments are read through <paramref name="readAttachment"/>.</summary>
    public static Task<int> ExportItemsAsync(Stream output, KdbxExportOptions options, IReadOnlyList<Source> items,
        Func<VaultAttachment, CancellationToken, Task<byte[]>> readAttachment, CancellationToken ct = default) =>
        ExportItemsAsync(output, options, items, readAttachment, KdbxKdf.Default, ct);

    internal static async Task<int> ExportItemsAsync(Stream output, KdbxExportOptions options, IReadOnlyList<Source> items,
        Func<VaultAttachment, CancellationToken, Task<byte[]>> readAttachment, KdbxKdf kdf, CancellationToken ct)
    {
        VaultKeyring.ValidateNewPassword(options.Password);
        var binaries = new List<byte[]>();
        var binaryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new List<KdbxGroup>();
        var count = 0;
        try
        {
            foreach (var kind in Enum.GetValues<VaultItemKind>())
            {
                var entries = new List<KdbxEntry>();
                foreach (var entry in items.Where(e => !e.Trashed && e.Item.Kind == kind))
                {
                    entries.Add(await ToEntryAsync(entry.Uid, entry.Item, options, binaries, binaryIndex, readAttachment, ct).ConfigureAwait(false));
                    count++;
                }
                if (entries.Count > 0) groups.Add(new KdbxGroup(GuidFor("group:" + kind), GroupName(kind), entries, []));
            }
            if (options.IncludeTrash)
            {
                var trashed = new List<KdbxEntry>();
                foreach (var entry in items.Where(e => e.Trashed))
                    trashed.Add(await ToEntryAsync(entry.Uid, entry.Item, options, binaries, binaryIndex, readAttachment, ct).ConfigureAwait(false));
                if (trashed.Count > 0) groups.Add(new KdbxGroup(GuidFor("group:trash"), "Trash", trashed, []));
            }

            var root = new KdbxGroup(GuidFor("group:root"), "Helm Vault", [], groups);
            await Task.Run(() => KdbxWriter.Write(output, options.Password, "Helm Vault", root, binaries, kdf), ct).ConfigureAwait(false);
            return count;
        }
        finally
        {
            foreach (var binary in binaries) CryptographicOperations.ZeroMemory(binary);
        }
    }

    private static async Task<KdbxEntry> ToEntryAsync(string uid, VaultItem item, KdbxExportOptions options, List<byte[]> binaries,
        Dictionary<string, int> binaryIndex, Func<VaultAttachment, CancellationToken, Task<byte[]>> readAttachment, CancellationToken ct)
    {
        // One at a time: the entries share the list of attachments.
        var history = new List<KdbxEntry>();
        if (options.IncludeHistory)
        {
            foreach (var version in item.History.Reverse())
                history.Add(await ToEntryCoreAsync(uid, version.Item, options, binaries, binaryIndex, readAttachment, [], ct).ConfigureAwait(false));
        }
        return await ToEntryCoreAsync(uid, item, options, binaries, binaryIndex, readAttachment, history, ct).ConfigureAwait(false);
    }

    private static async Task<KdbxEntry> ToEntryCoreAsync(string uid, VaultItem item, KdbxExportOptions options, List<byte[]> binaries,
        Dictionary<string, int> binaryIndex, Func<VaultAttachment, CancellationToken, Task<byte[]>> readAttachment,
        IReadOnlyList<KdbxEntry> history, CancellationToken ct)
    {
        var strings = new List<KdbxString> { new("Title", item.Title, false) };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Title" };
        // A login's first username, password and URL become KeePass's standard fields; everything else is a custom field.
        var mapped = new HashSet<int>();
        string Take(VaultFieldKind kind)
        {
            var index = item.Fields.ToList().FindIndex(f => f.Kind == kind);
            if (index < 0) return "";
            mapped.Add(index);
            return item.Fields[index].Value;
        }

        if (item.Kind == VaultItemKind.Login)
        {
            strings.Add(new("UserName", Take(VaultFieldKind.Username), false));
            strings.Add(new("Password", Take(VaultFieldKind.Password), true));
            strings.Add(new("URL", Take(VaultFieldKind.Url), false));
            used.UnionWith(["UserName", "Password", "URL"]);
        }
        else if (item.Kind == VaultItemKind.Token)
        {
            // The token is KeePass's password, so it is protected and copied like one there too.
            var index = item.Fields.ToList().FindIndex(f => f.IsSecret && f.Kind != VaultFieldKind.Totp);
            if (index >= 0) mapped.Add(index);
            strings.Add(new("Password", index >= 0 ? item.Fields[index].Value : "", true));
            used.Add("Password");
        }
        strings.Add(new("Notes", item.Notes, false));
        used.Add("Notes");

        foreach (var field in item.Fields.Where((_, i) => !mapped.Contains(i)))
        {
            // KeePassXC (and KeePass 2.x plugins) read a two-factor secret from "otp" as an otpauth:// link.
            if (field.Kind == VaultFieldKind.Totp && !used.Contains("otp") && Totp.Parse(field.Value) is { } totp)
            {
                strings.Add(new("otp", Totp.ToUri(totp, item.Title), true));
                used.Add("otp");
                continue;
            }
            var key = UniqueKey(field.Name, used);
            strings.Add(new(key, field.Value, field.IsSecret));
        }

        var attachments = new List<(string, int)>();
        if (options.IncludeDocuments)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attachment in item.Attachments)
            {
                if (!binaryIndex.TryGetValue(attachment.Blob.Id, out var index))
                {
                    index = binaries.Count;
                    binaries.Add(await readAttachment(attachment, ct).ConfigureAwait(false));
                    binaryIndex[attachment.Blob.Id] = index;
                }
                attachments.Add((UniqueKey(attachment.Name, names), index));
            }
        }

        return new KdbxEntry(GuidFor("item:" + uid), strings, attachments, item.Tags,
            FromMs(item.CreatedAtMs), FromMs(item.ModifiedAtMs), history);
    }

    private static string UniqueKey(string name, HashSet<string> used)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Field" : name.Trim();
        if (Standard.Contains(baseName) && used.Contains(baseName)) baseName += " (Helm)";
        var key = baseName;
        for (var n = 2; !used.Add(key); n++) key = $"{baseName} ({n})";
        return key;
    }

    private static string GroupName(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => "Logins",
        VaultItemKind.Note => "Secure notes",
        VaultItemKind.Card => "Cards",
        VaultItemKind.Identity => "Identities",
        VaultItemKind.Document => "Documents",
        VaultItemKind.Token => "Tokens",
        _ => kind.ToString(),
    };

    /// <summary>Stable UUIDs, so exporting twice gives entries KeePass sees as the same ones.</summary>
    private static Guid GuidFor(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("helm-vault/kdbx|" + name)).AsSpan(0, 16));

    private static DateTimeOffset FromMs(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow;
}
