using Android.Content;
using Android.OS;
using Android.Provider;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;

namespace Helm.Modules.Vault.Platform;

/// <summary>The Android side of the vault screens: Storage Access Framework pickers, the viewer intent, the clipboard.</summary>
internal sealed class AndroidVaultPlatform : IVaultPlatform
{
    private readonly IDialogService _dialogs;
    private readonly IShellNavigation _navigation;
    private readonly ISettingsStore<VaultSettings> _settings;
    private readonly ILogger _logger;
    private readonly string _openFolder;
    private string? _copied;
    private CancellationTokenSource? _clear;

    public AndroidVaultPlatform(IDialogService dialogs, IShellNavigation navigation, VaultSession session, ISettingsStoreFactory settings,
        ILogger<AndroidVaultPlatform> logger)
    {
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        // Cache (not files): Android may also clear it, and it is where the FileProvider shares "vault-open/".
        _openFolder = Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "vault-open");
        WipeOpenedFiles();
        session.Locking += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ClearIfOurs();
            WipeOpenedFiles();
        });
    }

    private static Context Context => AndroidApp.Context;

    public async Task<VaultPickedFile?> PickFileAsync(CancellationToken ct)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
        if (!outcome.Ok || outcome.Data?.Data is not { } uri) return null;
        var name = DisplayName(uri) ?? "document";
        var mediaType = Context.ContentResolver!.GetType(uri) ?? Sizes.MediaTypeOf(name);
        var stream = Context.ContentResolver!.OpenInputStream(uri) ?? throw new IOException("The file cannot be read.");
        return new VaultPickedFile(name, mediaType, stream);
    }

    public async Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct)
    {
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mediaType);
        intent.PutExtra(Intent.ExtraTitle, suggestedName);
        var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
        if (!outcome.Ok || outcome.Data?.Data is not { } uri) return null;
        return Context.ContentResolver!.OpenOutputStream(uri, "wt") ?? throw new IOException("The file cannot be written.");
    }

    public async Task OpenFileAsync(string name, string mediaType, byte[] content, CancellationToken ct)
    {
        var folder = Path.Combine(_openFolder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(name));
        await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(true);
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(Context, Context.PackageName + ".updates", new Java.IO.File(path));
        var view = new Intent(Intent.ActionView);
        view.SetDataAndType(uri, mediaType);
        view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        try
        {
            Context.StartActivity(Intent.CreateChooser(view, name)!.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission));
        }
        catch (ActivityNotFoundException)
        {
            throw new InvalidOperationException($"No app on this phone opens {Path.GetExtension(name)} files. Use Save a copy instead.");
        }
    }

    public void CopySecret(string text)
    {
        Copy(text, sensitive: true);
        var seconds = _settings.Current.ClipboardClearSeconds;
        _clear?.Cancel();
        if (seconds <= 0) return;
        _copied = text;
        var cts = _clear = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(ClearIfOurs);
        }, TaskScheduler.Default);
    }

    public void CopyText(string text) => Copy(text, sensitive: false);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => _dialogs.ConfirmAsync(title, message, confirmText);

    /// <summary>A folder (for example in Google Drive) chosen with the system picker; Helm keeps access to it.</summary>
    public async Task<string?> PickBackupLocationAsync(CancellationToken ct)
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
        if (!outcome.Ok || outcome.Data?.Data is not { } uri) return null;
        Context.ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        return uri.ToString();
    }

    /// <summary>Saves the kit as a text file where the user chooses (print it from there, or keep it offline).</summary>
    public async Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct)
    {
        // The recovery key opens the whole vault without the password: say so before it lands in Drive or Downloads.
        if (!await _dialogs.ConfirmAsync("Save the Emergency Kit",
                "This file holds the recovery key, which opens your whole vault without the password. Save it where you can print it or move it to a USB stick, then delete the file. Do not keep it in Google Drive, email or chat.",
                "Save").ConfigureAwait(true))
            return;
        var stream = await CreateFileAsync("Helm Vault Emergency Kit.txt", "text/plain", ct).ConfigureAwait(true);
        if (stream is null) return;
        await using (stream)
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(kit.Text).ConfigureAwait(true);
    }

    public void ShowVault() => _navigation.ShowPage(typeof(VaultContentPage));

    private void Copy(string text, bool sensitive)
    {
        if (Context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard) return;
        var clip = ClipData.NewPlainText(sensitive ? "Helm Vault" : "Helm", text)!;
        if (sensitive)
        {
            // Android 13+: the clipboard preview and keyboards hide the value.
            var extras = new PersistableBundle();
            extras.PutBoolean(OperatingSystem.IsAndroidVersionAtLeast(33) ? ClipDescription.ExtraIsSensitive : "android.content.extra.IS_SENSITIVE", true);
            clip.Description!.Extras = extras;
        }
        clipboard.PrimaryClip = clip;
    }

    private void ClearIfOurs()
    {
        _clear?.Cancel();
        if (_copied is null) return;
        try
        {
            if (Context.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard && clipboard.PrimaryClip?.GetItemAt(0)?.Text == _copied)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(28)) clipboard.ClearPrimaryClip();
                else clipboard.PrimaryClip = ClipData.NewPlainText("", "");
            }
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Android 10+ lets only the app in the foreground read the clipboard.
            _logger.LogDebug(ex, "Clipboard not readable in the background");
        }
        _copied = null;
    }

    private void WipeOpenedFiles()
    {
        try
        {
            if (Directory.Exists(_openFolder)) Directory.Delete(_openFolder, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogInformation(ex, "Could not wipe opened vault documents yet");
        }
    }

    private static string? DisplayName(AndroidUri uri)
    {
        using var cursor = Context.ContentResolver!.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
        return cursor is not null && cursor.MoveToFirst() ? cursor.GetString(0) : null;
    }

    private static string SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        return safe.Length == 0 ? "document" : safe;
    }
}
