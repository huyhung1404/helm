using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.Ui;
using Helm.Modules.Vault.ViewModels;
using Helm.Ui.Windows;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Vault.Platform;

/// <summary>Windows-only Vault preferences (settings/vault-windows.json): the auto-type shortcut and what it types.</summary>
public sealed class VaultWindowsSettings : IVersionedSettings
{
    public const string StoreId = "vault-windows";

    /// <summary>Ctrl+Alt+A, as in KeePass.</summary>
    public static readonly HotkeyGesture DefaultAutoTypeHotkey = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'A');

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public HotkeyGesture AutoTypeHotkey { get; set; } = DefaultAutoTypeHotkey;

    /// <summary>Press Enter after the password (signs in at once on most sites).</summary>
    public bool AutoTypePressEnter { get; set; }
}

/// <summary>
/// Auto-type: the shortcut types a login into the window in front, like KeePass. Helm reads which site a browser shows
/// (its address bar, through UI Automation) or which app it is, and types the username, Tab and the password with
/// Unicode key presses (never through the clipboard). With one sure match it types at once; otherwise a small picker
/// lists the likely logins, with a search. A locked vault opens Helm to unlock first.
/// </summary>
public sealed class AutoTypeService(
    VaultSession session,
    VaultStore store,
    IWindowService windows,
    IBrowserSites sites,
    IVaultPlatform platform,
    IMonitorService monitors,
    IUiDispatcher ui,
    ISettingsStoreFactory settings,
    IServiceProvider services,
    ILogger<AutoTypeService> logger)
{
    private readonly ISettingsStore<VaultWindowsSettings> _settings = settings.Get<VaultWindowsSettings>(VaultWindowsSettings.StoreId);
    private AutoTypePicker? _picker;
    private int _busy;

    public ISettingsStore<VaultWindowsSettings> Settings => _settings;

    /// <summary>The shortcut was pressed (hotkey thread): look at the window in front, then match and type.</summary>
    public void Trigger()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var target = windows.GetForegroundWindow();
        _ = Task.Run(async () =>
        {
            try
            {
                if (target == 0 || windows.GetProcessName(target).Equals("Helm", StringComparison.OrdinalIgnoreCase)) return;
                var title = windows.GetTitle(target);
                var process = windows.GetProcessName(target);
                var url = sites.Address(target, process);
                await ui.InvokeAsync(() => Offer(target, title, process, url)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-type failed");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private void Offer(nint target, string title, string process, string? url)
    {
        if (session.State != VaultState.Unlocked)
        {
            platform.ShowVault();
            Notify("Unlock the vault", $"Then press {_settings.Current.AutoTypeHotkey} again in the sign-in window.");
            return;
        }
        session.Touch();
        var host = VaultAutofill.HostOf(url);
        var entries = store.Items();
        var sure = VaultAutofill.Match(entries, new AutofillTarget(host, host is null ? process : null, title));
        if (sure.Count == 1)
        {
            _ = TypeAsync(target, sure[0].Entry);
            return;
        }
        var likely = sure.Concat(VaultAutofill.Suggest(entries, title).Where(s => sure.All(m => m.Entry.Uid != s.Entry.Uid))).ToList();
        _picker ??= new AutoTypePicker(windows, monitors);
        _picker.Show(likely, entries.Where(e => e.Item.Kind == VaultItemKind.Login && e.Item.Password is { Length: > 0 }).ToList(),
            host is null ? $"Offer it in {process} next time" : null,
            (entry, remember) =>
            {
                if (remember && host is null) VaultAutofill.RememberApp(store, entry.Uid, VaultAutofill.WindowsAppScheme, process);
                _ = TypeAsync(target, entry);
            });
    }

    /// <summary>Waits for the shortcut's keys to be released, brings the window back, checks it is still that one, types.</summary>
    private async Task TypeAsync(nint target, VaultEntry entry)
    {
        var item = entry.Item;
        for (var i = 0; i < 150 && InputSimulator.AnyModifierDown(); i++) await Task.Delay(20).ConfigureAwait(true);
        windows.Activate(target);
        await Task.Delay(150).ConfigureAwait(true);
        if (windows.GetForegroundWindow() != target)
        {
            Notify("Nothing was typed", "The sign-in window was not in front any more. Click into it and press the shortcut again.");
            return;
        }
        if (item.Username is { Length: > 0 } username)
        {
            InputSimulator.TypeText(username);
            InputSimulator.PressKey(0x09); // Tab
        }
        InputSimulator.TypeText(item.Password ?? "");
        if (_settings.Current.AutoTypePressEnter) InputSimulator.PressKey(0x0D);
        session.Touch();
        logger.LogInformation("Auto-typed a login into {Process}", windows.GetProcessName(target));
        // The two-factor step usually follows: its code is ready to paste.
        if (item.Totp is { } totp)
        {
            platform.CopySecret(totp.Code(DateTimeOffset.UtcNow));
            Notify("One-time code copied", $"Paste it (Ctrl+V) when {item.Title} asks for it.");
        }
    }

    private void Notify(string title, string message)
    {
        try
        {
            (services.GetService(typeof(IUserNotifications)) as IUserNotifications)?.Show(title, message);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not show a notification");
        }
    }

}

/// <summary>
/// The auto-type picker: the shared <see cref="AutoTypePickerView"/> in a floating Avalonia window over the app to type
/// into (<see cref="AvaloniaFloatingPanel"/>, like Quick Capture's box). It takes the keyboard while it is open; choosing
/// a login hides it first, so the sign-in window can get the focus back before anything is typed.
/// </summary>
internal sealed class AutoTypePicker
{
    private readonly AutoTypePickerViewModel _viewModel = new();
    private readonly AvaloniaFloatingPanel _panel;
    private Action<VaultEntry, bool>? _chosen;

    public AutoTypePicker(IWindowService windows, IMonitorService monitors)
    {
        var view = new AutoTypePickerView(_viewModel);
        _panel = new AvaloniaFloatingPanel(windows, monitors, 460, "Auto-type") { Panel = view };
        _panel.Summoned += (_, _) => view.FocusSearch();
        // Esc or a click elsewhere: nothing is typed.
        _panel.Dismissed += (_, _) => _chosen = null;
        _viewModel.Chosen += (_, choice) =>
        {
            var chosen = _chosen;
            _chosen = null;
            _panel.Dismiss();
            chosen?.Invoke(choice.Entry, choice.Remember);
        };
    }

    public void Show(IReadOnlyList<AutofillMatch> likely, IReadOnlyList<VaultEntry> all, string? rememberText, Action<VaultEntry, bool> chosen)
    {
        _viewModel.Load(likely.Select(m => m.Entry).ToList(), all, rememberText);
        _chosen = chosen;
        _panel.Summon();
    }
}
