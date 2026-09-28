using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Helm.Core.Settings;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Helm.Modules.Vault.Platform;

/// <summary>The Windows side of the vault screens: dialogs, files, the protected clipboard, printing.</summary>
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
        session.Locking += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _clipboard.ClearIfOurs();
            WipeOpenedFiles();
        });
    }

    private static Window? Owner => Application.Current?.MainWindow;

    public Task<VaultPickedFile?> PickFileAsync(CancellationToken ct)
    {
        var dialog = new OpenFileDialog { Title = "Attach a document", CheckFileExists = true };
        if (dialog.ShowDialog(Owner) != true) return Task.FromResult<VaultPickedFile?>(null);
        var name = Path.GetFileName(dialog.FileName);
        return Task.FromResult<VaultPickedFile?>(new VaultPickedFile(name, Sizes.MediaTypeOf(name), File.OpenRead(dialog.FileName)));
    }

    public Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct)
    {
        var extension = Path.GetExtension(suggestedName);
        var dialog = new SaveFileDialog
        {
            Title = "Save",
            FileName = suggestedName,
            DefaultExt = extension,
            Filter = extension.Length > 0 ? $"{extension.TrimStart('.').ToUpperInvariant()} file|*{extension}|All files|*.*" : "All files|*.*",
        };
        if (dialog.ShowDialog(Owner) != true) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(File.Create(dialog.FileName));
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

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new WpfUiMessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            MinWidth = 380,
            Owner = Owner,
        };
        return await box.ShowDialogAsync().ConfigureAwait(true) == WpfUiMessageBoxResult.Primary;
    }

    public Task<string?> PickBackupLocationAsync(CancellationToken ct)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the backup folder (for example a Google Drive or OneDrive folder, or a USB stick)",
            InitialDirectory = _settings.Current.BackupLocation ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return Task.FromResult(dialog.ShowDialog(Owner) == true ? dialog.FolderName : null);
    }

    /// <summary>Prints the kit (choose "Microsoft Print to PDF" to save it as a file instead).</summary>
    public Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct)
    {
        var print = new PrintDialog();
        if (print.ShowDialog() != true) return Task.CompletedTask;
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(56),
            ColumnWidth = double.PositiveInfinity,
        };
        foreach (var line in kit.Text.Split(Environment.NewLine))
        {
            var isKey = line.Trim() == kit.RecoveryKey;
            document.Blocks.Add(new Paragraph(new Run(line))
            {
                Margin = new Thickness(0, 0, 0, 4),
                FontFamily = isKey ? new FontFamily("Consolas") : document.FontFamily,
                FontSize = isKey ? 15 : line.StartsWith("HELM VAULT", StringComparison.Ordinal) ? 20 : 13,
                FontWeight = isKey || line.StartsWith("HELM VAULT", StringComparison.Ordinal) ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
        print.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "Helm Vault Emergency Kit");
        return Task.CompletedTask;
    }

    public void ShowVault() => _services.GetRequiredService<Helm.Core.Services.IShellNavigation>().ShowPage(typeof(VaultContentPage));

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
