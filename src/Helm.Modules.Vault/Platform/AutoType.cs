using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
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
    IVaultPlatform platform,
    IMonitorService monitors,
    IUiDispatcher ui,
    ISettingsStoreFactory settings,
    IServiceProvider services,
    ILogger<AutoTypeService> logger)
{
    private static readonly string[] Chromium = ["chrome", "msedge", "brave", "opera", "vivaldi", "browser", "arc", "thorium"];
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
                var url = BrowserAddress(target, process);
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

    /// <summary>What a browser's address bar shows ("github.com/login"), or null for any other window.</summary>
    private string? BrowserAddress(nint hwnd, string process)
    {
        var name = process.ToLowerInvariant();
        var isFirefox = name is "firefox" or "librewolf" or "waterfox" or "floorp" or "zen";
        if (!isFirefox && !Chromium.Contains(name)) return null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            AutomationElement? bar = isFirefox
                ? root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"))
                // Chromium: the first edit box of the window is the address bar (the page's own come later).
                : root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            return bar?.GetCurrentPropertyValue(ValuePattern.ValueProperty) as string is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            logger.LogDebug(ex, "Could not read the address of {Process}", process);
            return null;
        }
    }
}

/// <summary>The auto-type picker: the likely logins first, a search over all of them, Enter or a click types.</summary>
internal sealed class AutoTypePicker : FloatingPanelWindow
{
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ListBox _list = new() { MaxHeight = 320, BorderThickness = new Thickness(0) };
    private readonly CheckBox _remember = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _empty = new() { Margin = new Thickness(4, 4, 4, 8), Text = "No login matches.", Visibility = Visibility.Collapsed };
    private IReadOnlyList<VaultEntry> _all = [];
    private IReadOnlyList<VaultEntry> _likely = [];
    private Action<VaultEntry, bool>? _chosen;

    public AutoTypePicker(IWindowService windows, IMonitorService monitors) : base(windows, monitors, 460)
    {
        Title = "Auto-type";
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search the vault");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var header = new TextBlock { Text = "Type which login?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(header);
        panel.Children.Add(_search);
        panel.Children.Add(_empty);
        panel.Children.Add(_list);
        panel.Children.Add(_remember);
        Panel = panel;

        _search.TextChanged += (_, _) => Filter();
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _list.Items.Count > 0)
            {
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.Items.Count - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && _list.Items.Count > 0)
            {
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Choose();
                e.Handled = true;
            }
        };
        _list.MouseDoubleClick += (_, _) => Choose();
        _list.PreviewMouseLeftButtonUp += (_, _) => Choose();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Choose();
            e.Handled = true;
        };
    }

    public void Show(IReadOnlyList<AutofillMatch> likely, IReadOnlyList<VaultEntry> all, string? rememberText, Action<VaultEntry, bool> chosen)
    {
        _likely = likely.Select(m => m.Entry).ToList();
        _all = all.OrderBy(e => e.Item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        _chosen = chosen;
        _remember.Content = rememberText;
        _remember.IsChecked = rememberText is not null;
        _remember.Visibility = rememberText is null ? Visibility.Collapsed : Visibility.Visible;
        _search.Text = "";
        Filter();
        Summon();
        _search.Focus();
    }

    private void Filter()
    {
        var terms = Helm.Core.Text.TextSearch.Terms(_search.Text);
        var shown = terms.Count == 0
            ? _likely.Concat(_all.Where(e => _likely.All(l => l.Uid != e.Uid))).Take(30)
            : _all.Where(e => Helm.Core.Text.TextSearch.Score(terms, e.Item.Title, e.Item.Username) > 0).Take(30);
        _list.Items.Clear();
        foreach (var entry in shown)
        {
            var row = new StackPanel { Tag = entry, Margin = new Thickness(4, 2, 4, 2) };
            row.Children.Add(new TextBlock { Text = entry.Item.Title, FontWeight = FontWeights.SemiBold });
            var detail = new TextBlock { Text = entry.Item.Username ?? "", FontSize = 12 };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            row.Children.Add(detail);
            _list.Items.Add(new ListBoxItem { Content = row, Tag = entry });
        }
        _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Choose()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: VaultEntry entry }) return;
        var remember = _remember.IsChecked == true;
        var chosen = _chosen;
        _chosen = null;
        Dismiss();
        chosen?.Invoke(entry, remember);
    }
}
