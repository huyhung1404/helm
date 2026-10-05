using System.Globalization;
using System.Text;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>A file the user picked to attach. The content stream is read once and disposed by the caller.</summary>
public sealed record VaultPickedFile(string Name, string MediaType, Stream Content);

/// <summary>What the user must keep outside the computer: the recovery key and how to use it.</summary>
public sealed record EmergencyKit(string VaultId, string RecoveryId, string RecoveryKey, DateTimeOffset CreatedAt)
{
    /// <summary>The printable text (plain, so it prints and saves the same everywhere).</summary>
    public string Text => string.Join(Environment.NewLine,
        "HELM VAULT — EMERGENCY KIT",
        "",
        "Keep this page somewhere safe and offline (a drawer, a safe). Anyone with it and your backups can open your vault.",
        "If you saved it as a file (PDF or text), print it and delete the file: never keep it in a folder that syncs to the",
        "cloud, in email or in chat.",
        "",
        $"Created:          {CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)}",
        $"Vault id:         {VaultId}",
        $"Recovery key id:  {RecoveryId}",
        "",
        "Recovery key:",
        "",
        "    " + RecoveryKey,
        "",
        "Vault password:   ______________________________ (write it here only if this page stays locked away)",
        "",
        "If you forget the vault password: open Vault, choose \"Use the recovery key\", type the key above, then set a",
        "new password. Without Helm: run helm-vault-restore on your backup folder (see its README.txt).",
        "",
        "If Helm ever shows a different recovery key id than the one above, this page is out of date: print the new one.");
}

/// <summary>
/// What the vault screens need from the platform (WPF on Windows, Avalonia on Android). Implementations run on the
/// UI thread and never throw for cancellations: they return null or false instead.
/// </summary>
public interface IVaultPlatform
{
    Task<VaultPickedFile?> PickFileAsync(CancellationToken ct);

    /// <summary>Asks where to save a file (a document, an export) and opens it for writing; null when cancelled.</summary>
    Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct);

    /// <summary>
    /// Opens a decrypted document with the system viewer. The copy lives in the app's private temp folder, which is
    /// wiped when the vault locks and when the app starts.
    /// </summary>
    Task OpenFileAsync(string name, string mediaType, byte[] content, CancellationToken ct);

    /// <summary>Copies a secret with the clipboard protections: no history or cloud clipboard, cleared after a while.</summary>
    void CopySecret(string text);

    void CopyText(string text);

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Lets the user pick the backup folder (PC path, or Android tree URI); null when cancelled.</summary>
    Task<string?> PickBackupLocationAsync(CancellationToken ct);

    /// <summary>Prints or saves the Emergency Kit.</summary>
    Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct);

    /// <summary>Brings the vault itself to the front (its window on PC, its screen on Android).</summary>
    void ShowVault();
}

public static class Sizes
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    public static string MediaTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream",
    };

    public static string Join(params string?[] parts) => new StringBuilder().AppendJoin(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))).ToString();
}
