namespace Helm.Modules.Vault.Backup;

/// <summary>
/// Where a backup repository lives: a folder on PC (OneDrive, Google Drive for desktop, a USB stick) or a Storage
/// Access Framework tree on Android. Paths are relative, with '/' separators. The repository is append-only, so a
/// target never needs to modify a file in place: new files are written whole, then made visible under their name.
/// </summary>
public interface IBackupTarget
{
    /// <summary>Shown to the user, e.g. "G:\My Drive\Backups".</summary>
    string DisplayName { get; }

    Task<bool> ExistsAsync(string path, CancellationToken ct);

    /// <summary>Names (not paths) of the files directly in <paramref name="directory"/>; empty when it does not exist.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken ct);

    /// <summary>Names of the sub-folders of <paramref name="directory"/>.</summary>
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct);

    /// <returns>Null when the file does not exist.</returns>
    Task<byte[]?> ReadAsync(string path, CancellationToken ct);

    /// <summary>Writes the whole file so that a reader never sees it half-written (temp file, then rename).</summary>
    Task WriteAsync(string path, byte[] data, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);

    Task DeleteDirectoryAsync(string directory, CancellationToken ct);

    /// <summary>Removes temp files (from a write a crash interrupted) older than <paramref name="olderThan"/>, anywhere below the directory.</summary>
    /// <returns>How many were removed.</returns>
    Task<int> DeleteStaleTempFilesAsync(string directory, TimeSpan olderThan, CancellationToken ct);
}

/// <summary>A folder on this computer (including folders that a cloud client such as Google Drive or OneDrive syncs).</summary>
public sealed class FolderBackupTarget(string root) : IBackupTarget
{
    public string Root { get; } = root;

    public string DisplayName => Root;

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(File.Exists(Full(path)));

    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken ct)
    {
        var full = Full(directory);
        IReadOnlyList<string> names = Directory.Exists(full)
            ? Directory.EnumerateFiles(full).Select(Path.GetFileName).Where(n => n is not null && !n.EndsWith(".tmp", StringComparison.Ordinal)).Select(n => n!).ToList()
            : [];
        return Task.FromResult(names);
    }

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct)
    {
        var full = Full(directory);
        IReadOnlyList<string> names = Directory.Exists(full) ? Directory.EnumerateDirectories(full).Select(d => Path.GetFileName(d)!).ToList() : [];
        return Task.FromResult(names);
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : null;
    }

    public async Task WriteAsync(string path, byte[] data, CancellationToken ct)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        File.Move(temp, full, overwrite: true);
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string directory, CancellationToken ct)
    {
        var full = Full(directory);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        return Task.CompletedTask;
    }

    public Task<int> DeleteStaleTempFilesAsync(string directory, TimeSpan olderThan, CancellationToken ct)
    {
        var full = Full(directory);
        if (!Directory.Exists(full)) return Task.FromResult(0);
        var cutoff = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(full, "*.tmp", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            File.Delete(file);
            removed++;
        }
        return Task.FromResult(removed);
    }

    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        // Paths come from the repository itself (snapshot and blob names); never let one escape the root.
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full != root.TrimEnd(Path.DirectorySeparatorChar))
            throw new ArgumentException($"'{path}' is outside the backup folder.", nameof(path));
        return full;
    }
}
