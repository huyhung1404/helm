using System.Text.Json;
using System.Text.Json.Serialization;
using Helm.Core.Sync;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;

namespace Helm.VaultRestore;

/// <summary>
/// The commands of helm-vault-restore. Everything it needs is in the backup folder plus the vault password or the
/// recovery key; it never talks to a server.
/// </summary>
internal sealed class RestoreTool(TextWriter output, Func<string, string> readSecret)
{
    private const string Usage = """
        helm-vault-restore: open a Helm Vault backup without Helm.

          helm-vault-restore list   <backup folder>
          helm-vault-restore export <backup folder> <empty output folder> [options]
              Writes items.json and every document, DECRYPTED, into the output folder. Keep that folder safe
              and delete it when you are done.
          helm-vault-restore kdbx   <backup folder> <new file.kdbx> [options]
              Converts the backup into a KeePass database (KeePassXC, KeePass, KeePassDX) protected by a new
              password. Nothing is written in clear.

        Options:
          --snapshot <name>   a snapshot from "list" (default: the newest)
          --vault <id>        when the folder holds backups of several vaults
          --recovery          unlock with the recovery key from the Emergency Kit instead of the password
          --trash             include items that were in the trash
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            if (args.Length < 2) return Fail(Usage);
            var command = args[0];
            var options = Options.Parse(args[1..]);
            var repositories = await FindAsync(options.Positional[0], ct).ConfigureAwait(false);
            switch (command)
            {
                case "list":
                    List(repositories);
                    return 0;
                case "export" when options.Positional.Count == 2:
                    return await ExportAsync(Pick(repositories, options), options, options.Positional[1], ct).ConfigureAwait(false);
                case "kdbx" when options.Positional.Count == 2:
                    return await KdbxAsync(Pick(repositories, options), options, options.Positional[1], ct).ConfigureAwait(false);
                default:
                    return Fail(Usage);
            }
        }
        catch (Exception ex) when (ex is VaultKeyException or ToolException or BlobUnavailableException or IOException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException)
        {
            return Fail(ex.Message);
        }
    }

    private void List(IReadOnlyList<VaultBackupRepository> repositories)
    {
        foreach (var repository in repositories)
        {
            output.WriteLine($"Vault {repository.VaultId} (recovery key id {repository.Keyring.RecoveryId}), in {repository.Directory}");
            foreach (var name in repository.Snapshots)
                output.WriteLine($"  {name}   {VaultBackupRepository.SnapshotTime(name):yyyy-MM-dd HH:mm:ss} UTC");
        }
    }

    private async Task<int> ExportAsync(VaultBackupRepository repository, Options options, string destination, CancellationToken ct)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new ToolException($"{destination} is not empty. Choose an empty folder, so nothing is overwritten.");
        using var key = Unlock(repository, options);
        var (snapshotName, items) = await ReadAsync(repository, options, key, ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.Combine(destination, "files"));
        var exported = new List<object>();
        foreach (var (recordId, record, item) in items)
        {
            var files = new List<object>();
            foreach (var attachment in item.Attachments)
            {
                var folder = Path.Combine(destination, "files", recordId);
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, SafeName(attachment.Name));
                await using (var stream = File.Create(file))
                    await BlobStore.DecryptAsync(attachment.Blob, i => repository.ReadChunkAsync(attachment.Blob.Id, i, ct), stream, null, ct).ConfigureAwait(false);
                files.Add(new { attachment.Name, attachment.MediaType, attachment.Size, File = Path.GetRelativePath(destination, file).Replace('\\', '/') });
            }
            exported.Add(new
            {
                Id = record.Uid,
                item.Kind,
                item.Title,
                Fields = item.Fields.Select(f => new { f.Name, f.Value, f.Kind }),
                item.Notes,
                item.Tags,
                item.Favorite,
                record.Trashed,
                Created = Time(item.CreatedAtMs),
                Modified = Time(item.ModifiedAtMs),
                Attachments = files,
                History = item.History.Select(h => new
                {
                    Saved = Time(h.SavedAtMs),
                    h.Item.Title,
                    Fields = h.Item.Fields.Select(f => new { f.Name, f.Value, f.Kind }),
                    h.Item.Notes,
                    Attachments = h.Item.Attachments.Select(a => a.Name),
                }),
            });
        }
        await File.WriteAllTextAsync(Path.Combine(destination, "items.json"), JsonSerializer.Serialize(exported, Json), ct).ConfigureAwait(false);
        output.WriteLine($"Exported {exported.Count} items from {snapshotName} into {destination}.");
        output.WriteLine("The files there are NOT encrypted. Move what you need somewhere safe and delete the folder.");
        return 0;
    }

    private async Task<int> KdbxAsync(VaultBackupRepository repository, Options options, string destination, CancellationToken ct)
    {
        if (File.Exists(destination)) throw new ToolException($"{destination} already exists.");
        using var key = Unlock(repository, options);
        var (snapshotName, items) = await ReadAsync(repository, options, key, ct).ConfigureAwait(false);
        var password = readSecret("New password for the KeePass file: ");
        if (readSecret("Type it again: ") != password) throw new ToolException("The two passwords differ.");

        var sources = items.Select(i => new VaultKdbxExporter.Source(i.Record.Uid, i.Item, i.Record.Trashed)).ToList();
        int count;
        await using (var stream = File.Create(destination))
        {
            count = await VaultKdbxExporter.ExportItemsAsync(stream, new KdbxExportOptions(password, IncludeTrash: options.Trash), sources,
                async (attachment, token) =>
                {
                    using var memory = new MemoryStream();
                    await BlobStore.DecryptAsync(attachment.Blob, i => repository.ReadChunkAsync(attachment.Blob.Id, i, token), memory, null, token).ConfigureAwait(false);
                    return memory.ToArray();
                }, ct).ConfigureAwait(false);
        }
        output.WriteLine($"Wrote {count} items from {snapshotName} into {destination}.");
        return 0;
    }

    private async Task<(string Snapshot, IReadOnlyList<(string RecordId, VaultItemRecord Record, VaultItem Item)> Items)> ReadAsync(
        VaultBackupRepository repository, Options options, VaultKey key, CancellationToken ct)
    {
        var name = options.Snapshot ?? repository.Snapshots.FirstOrDefault() ?? throw new ToolException("This backup has no snapshot.");
        if (!repository.Snapshots.Contains(name)) throw new ToolException($"There is no snapshot {name}. Run \"list\" to see them.");
        var snapshot = await repository.ReadSnapshotAsync(name, key, ct).ConfigureAwait(false);
        var items = new List<(string, VaultItemRecord, VaultItem)>();
        var unreadable = 0;
        foreach (var record in snapshot.Records)
        {
            if (record.Record.Trashed && !options.Trash) continue;
            if (VaultStore.TryOpenWith(key, record.Record) is { } item) items.Add((record.Id, record.Record, item));
            else unreadable++;
        }
        if (unreadable > 0) output.WriteLine($"Warning: {unreadable} records of the snapshot could not be opened and were skipped.");
        return (name, items);
    }

    private VaultKey Unlock(VaultBackupRepository repository, Options options) => options.Recovery
        ? VaultKeyring.UnlockWithRecoveryKey(repository.Keyring, readSecret("Recovery key (HELMV-…): "))
        : VaultKeyring.UnlockWithPassword(repository.Keyring, readSecret("Vault password: "));

    private static async Task<IReadOnlyList<VaultBackupRepository>> FindAsync(string folder, CancellationToken ct)
    {
        var full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) throw new ToolException($"{folder} does not exist.");
        // Either the folder that contains HelmVault-… folders, or one of those itself.
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (name.StartsWith(VaultBackupRepository.Prefix, StringComparison.Ordinal)
            && await VaultBackupRepository.OpenAsync(new FolderBackupTarget(Path.GetDirectoryName(full)!), name, ct).ConfigureAwait(false) is { } single)
            return [single];
        var found = await VaultBackupRepository.FindAsync(new FolderBackupTarget(full), ct).ConfigureAwait(false);
        return found.Count > 0 ? found : throw new ToolException($"No Helm Vault backup in {folder}.");
    }

    private static VaultBackupRepository Pick(IReadOnlyList<VaultBackupRepository> repositories, Options options)
    {
        if (options.Vault is { } id)
            return repositories.FirstOrDefault(r => r.VaultId == id) ?? throw new ToolException($"No backup of vault {id} here.");
        return repositories.Count == 1 ? repositories[0] : throw new ToolException("This folder holds backups of several vaults. Pick one with --vault <id> (see \"list\").");
    }

    private static string SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c)).Trim('.', ' ');
        return safe.Length == 0 ? "file" : safe;
    }

    private static string? Time(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O") : null;

    private int Fail(string message)
    {
        output.WriteLine(message);
        return 1;
    }

    private sealed class ToolException(string message) : Exception(message);

    private sealed record Options(IReadOnlyList<string> Positional, string? Snapshot, string? Vault, bool Recovery, bool Trash)
    {
        public static Options Parse(string[] args)
        {
            var positional = new List<string>();
            string? snapshot = null, vault = null;
            bool recovery = false, trash = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--snapshot" when i + 1 < args.Length: snapshot = args[++i]; break;
                    case "--vault" when i + 1 < args.Length: vault = args[++i]; break;
                    case "--recovery": recovery = true; break;
                    case "--trash": trash = true; break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ToolException($"Unknown option {args[i]}.\n\n{Usage}");
                        positional.Add(args[i]);
                        break;
                }
            }
            if (positional.Count == 0) throw new ToolException(Usage);
            return new Options(positional, snapshot, vault, recovery, trash);
        }
    }
}
