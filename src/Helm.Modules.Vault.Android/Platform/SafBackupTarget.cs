using Android.Content;
using AndroidX.DocumentFile.Provider;
using Helm.Modules.Vault.Backup;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;

namespace Helm.Modules.Vault.Platform;

/// <summary>
/// A backup repository in a folder the user picked through the Storage Access Framework (Google Drive, a USB drive,
/// the phone's storage). SAF has no atomic rename-over, so a file is written under a temporary name, then the old one
/// is removed and the new one renamed; the repository never rewrites a file it needs, so a crash leaves only a temp.
/// </summary>
internal sealed class SafBackupTarget(DocumentFile root, string displayName) : IBackupTarget
{
    private static ContentResolver Resolver => AndroidApp.Context.ContentResolver!;

    public string DisplayName => displayName;

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(Find(path) is { IsFile: true });

    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(
        Find(directory) is { IsDirectory: true } folder
            ? (folder.ListFiles() ?? []).Where(f => f.IsFile && f.Name is { } n && !n.EndsWith(".tmp", StringComparison.Ordinal)).Select(f => f.Name!).ToList()
            : []);

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(
        Find(directory) is { IsDirectory: true } folder ? (folder.ListFiles() ?? []).Where(f => f.IsDirectory && f.Name is not null).Select(f => f.Name!).ToList() : []);

    public async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        if (Find(path) is not { IsFile: true } file) return null;
        await using var input = Resolver.OpenInputStream(file.Uri!) ?? throw new IOException($"{path} cannot be read.");
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    public async Task WriteAsync(string path, byte[] data, CancellationToken ct)
    {
        var (folderPath, name) = Split(path);
        var folder = EnsureDirectory(folderPath);
        var temp = folder.CreateFile("application/octet-stream", name + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp")
            ?? throw new IOException($"Cannot create a file in {displayName}.");
        await using (var output = Resolver.OpenOutputStream(temp.Uri!, "wt") ?? throw new IOException($"Cannot write {path}."))
            await output.WriteAsync(data, ct).ConfigureAwait(false);
        folder.FindFile(name)?.Delete();
        if (!temp.RenameTo(name)) throw new IOException($"Cannot finish writing {path}.");
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        Find(path)?.Delete();
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string directory, CancellationToken ct)
    {
        if (Find(directory) is { IsDirectory: true } folder) folder.Delete();
        return Task.CompletedTask;
    }

    private DocumentFile? Find(string path)
    {
        var current = root;
        foreach (var part in Parts(path))
        {
            current = current.FindFile(part);
            if (current is null) return null;
        }
        return current;
    }

    private DocumentFile EnsureDirectory(string path)
    {
        var current = root;
        foreach (var part in Parts(path))
            current = current.FindFile(part) ?? current.CreateDirectory(part) ?? throw new IOException($"Cannot create {part} in {displayName}.");
        return current;
    }

    private static string[] Parts(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Names come from the repository itself; never let one climb out of the chosen folder.
        if (parts.Any(p => p is "." or "..")) throw new ArgumentException($"'{path}' is outside the backup folder.", nameof(path));
        return parts;
    }

    private static (string Folder, string Name) Split(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
    }
}

/// <summary>Android: the backup location is a tree URI Helm has a persisted permission for.</summary>
internal sealed class SafBackupLocation : IVaultBackupLocation
{
    public IBackupTarget? Open(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var uri = AndroidUri.Parse(location);
        if (uri is null || !AndroidApp.Context.ContentResolver!.PersistedUriPermissions.Any(p => p.Uri?.Equals(uri) == true && p.IsWritePermission))
            return null;
        var root = DocumentFile.FromTreeUri(AndroidApp.Context, uri);
        return root is { IsDirectory: true } && root.CanWrite() ? new SafBackupTarget(root, root.Name ?? "Backup folder") : null;
    }
}
