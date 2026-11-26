using System.Diagnostics;
using System.IO;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Helm.Core.Settings;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Helm.Ui.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Vault.Platform;

/// <summary>The Windows side of the vault screens: dialogs, files, the protected clipboard, the Emergency Kit file.</summary>
internal sealed class WindowsVaultPlatform : IVaultPlatform
{
    private readonly IServiceProvider _services;
    private readonly SecureClipboard _clipboard;
    private readonly ISettingsStore<VaultSettings> _settings;
    private readonly ILogger _logger;
    private readonly string _openFolder;

    public WindowsVaultPlatform(IServiceProvider services, SecureClipboard clipboard, VaultSession session, ISettingsStoreFactory settings,
        ILogger<WindowsVaultPlatform> logger)
    {
        _services = services;
        _clipboard = clipboard;
        _logger = logger;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        _openFolder = Path.Combine(settings.Paths.Root, "vault-open");
        // Decrypted copies opened in other apps never outlive the unlocked vault (nor a crash: wiped at start too).
        WipeOpenedFiles();
        session.Locking += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _clipboard.ClearIfOurs();
            WipeOpenedFiles();
        });
    }

    /// <summary>The pickers open over the vault page's window when it is on screen, else over the window in front.</summary>
    private static Avalonia.Visual? Anchor => Ui.VaultDialogs.Anchor;

    public async Task<VaultPickedFile?> PickFileAsync(CancellationToken ct)
    {
        var picked = await DesktopWindows.PickFilesAsync("Attach a document", anchor: Anchor).ConfigureAwait(true);
        if (picked.Count == 0 || !File.Exists(picked[0])) return null;
        var name = Path.GetFileName(picked[0]);
        return new VaultPickedFile(name, Sizes.MediaTypeOf(name), File.OpenRead(picked[0]));
    }

    public async Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct)
    {
        var extension = Path.GetExtension(suggestedName);
        var all = new FilePickerFileType("All files") { Patterns = ["*.*"] };
        IReadOnlyList<FilePickerFileType> types = extension.Length > 0
            ? [new FilePickerFileType($"{extension.TrimStart('.').ToUpperInvariant()} file") { Patterns = [$"*{extension}"] }, all]
            : [all];
        var path = await DesktopWindows.PickSaveAsync("Save", suggestedName, types, Anchor).ConfigureAwait(true);
        return path is null ? null : File.Create(path);
    }

    public async Task OpenFileAsync(string name, string mediaType, byte[] content, CancellationToken ct)
    {
        // Out of Windows Search (which would keep the text of a PDF in its index long after the file is gone) and
        // hidden. New files inherit "not content indexed" from the folder; set it on the file too, before writing.
        Directory.CreateDirectory(_openFolder);
        var root = new DirectoryInfo(_openFolder);
        root.Attributes |= FileAttributes.NotContentIndexed | FileAttributes.Hidden;
        var folder = Directory.CreateDirectory(Path.Combine(_openFolder, Guid.NewGuid().ToString("N")[..8]));
        folder.Attributes |= FileAttributes.NotContentIndexed;
        var path = Path.Combine(folder.FullName, SafeName(name));
        // Empty first, marked, then filled: the indexer never sees the content of an unmarked file.
        File.Create(path).Dispose();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.NotContentIndexed);
        await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(true);
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No app opens {Extension} files", Path.GetExtension(name));
            throw new InvalidOperationException($"Windows has no app to open {Path.GetExtension(name)} files. Use Save as instead.");
        }
    }

    public void CopySecret(string text)
    {
        var seconds = _settings.Current.ClipboardClearSeconds;
        // Even with "never clear", a secret is kept out of the clipboard history and the cloud clipboard.
        _clipboard.Copy(text, seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);
    }

    public void CopyText(string text) => _clipboard.Copy(text, clearAfter: null);

    /// <summary>Helm's own confirmation sheet, drawn by the vault page that is on screen.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Ui.VaultDialogs.ConfirmAsync(title, message, confirmText);

    public async Task<string?> PickBackupLocationAsync(CancellationToken ct)
    {
        if (DesktopWindows.Storage(Anchor) is not { } storage) return null;
        var start = _settings.Current.BackupLocation is { Length: > 0 } current && Directory.Exists(current)
            ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the backup folder (for example a Google Drive or OneDrive folder, or a USB stick)",
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(start).ConfigureAwait(true),
        }).ConfigureAwait(true);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// Saves the kit as a text file where the user chooses, to print from there or keep offline (as on Android: the PC
    /// app has no print dialog of its own since it is Avalonia).
    /// </summary>
    public async Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct)
    {
        // The recovery key opens the whole vault without the password: say so before it lands in a synced folder.
        if (!await ConfirmAsync("Save the Emergency Kit",
                "This file holds the recovery key, which opens your whole vault without the password. Save it where you can print it or move it to a USB stick, then delete the file. Do not keep it in OneDrive, Google Drive, email or chat.",
                "Save").ConfigureAwait(true))
            return;
        var stream = await CreateFileAsync("Helm Vault Emergency Kit.txt", "text/plain", ct).ConfigureAwait(true);
        if (stream is null) return;
        await using (stream)
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(kit.Text).ConfigureAwait(true);
    }

    public void ShowVault() => _services.GetRequiredService<Helm.Core.Services.IShellNavigation>().ShowTool(VaultModule.ModuleId);

    private void WipeOpenedFiles()
    {
        try
        {
            if (Directory.Exists(_openFolder)) Directory.Delete(_openFolder, recursive: true);
        }
        catch (IOException ex)
        {
            // A viewer still has a file open; the next lock or start tries again.
            _logger.LogInformation(ex, "Some opened vault documents are still in use");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogInformation(ex, "Some opened vault documents are still in use");
        }
    }

    private static string SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        return safe.Length == 0 ? "document" : safe;
    }
}
