using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Secrets;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Ssh;

/// <summary>A server in the list, with the state of its session.</summary>
public sealed partial class SshHostRow : ObservableObject
{
    [ObservableProperty] private SshHost _host;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _state = "";

    public SshHostRow(SshHost host) => _host = host;

    public string Id => Host.Id;

    public string Title => Host.DisplayName;

    public string Target => Host.Target;

    public string AuthText => Host.Auth switch
    {
        SshAuthKind.Password => "Password",
        SshAuthKind.KeyFile => "Key " + Path.GetFileName(Host.KeyFile),
        SshAuthKind.Vault => $"Vault: {Host.VaultItemTitle} · {Host.VaultField}",
        _ => "This device's key",
    };

    /// <summary>The second line in the server list: the address (unless it is already the title) and how Helm signs in.</summary>
    public string Detail => string.IsNullOrWhiteSpace(Host.Name) ? AuthText : $"{Host.Target} · {AuthText}";

    partial void OnHostChanged(SshHost value)
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(AuthText));
    }
}

/// <summary>A way to sign in, as the editor offers it.</summary>
public sealed record SshAuthChoice(SshAuthKind Kind, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A server key this device accepted.</summary>
public sealed class SshKnownHostRow(SshKnownHost known)
{
    public SshKnownHost Known { get; } = known;

    public string Title => Known.Endpoint;

    public string Detail => $"{Known.Algorithm} · SHA256:{Known.Fingerprint}";
}

/// <summary>
/// The SSH tool, shared by the content page (servers and the terminal) and the settings page (servers, this device's
/// key, known server keys). Sessions live here, one per server, so they keep running while Helm shows another page.
/// Members are used on the UI thread; session events arrive on background threads and are posted back.
/// </summary>
public sealed partial class SshViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore<SshSettings> _settings;
    private readonly SshDeviceKey _deviceKey;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<SshViewModel> _logger;
    private readonly IVaultSecrets? _vault;
    private readonly Dictionary<string, SshSession> _sessions = new(StringComparer.Ordinal);
    // Servers whose key file turned out to need a passphrase: the page then shows the box for it.
    private readonly HashSet<string> _passphraseNeeded = new(StringComparer.Ordinal);
    private int _columns = 120;
    private int _rows = 32;
    private bool _loading;
    private bool _enabled = true;

    [ObservableProperty] private SshHostRow? _selectedHost;
    [ObservableProperty] private SshSession? _activeSession;
    [ObservableProperty] private string _sessionStatus = "";
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private int _fontSize;

    /// <summary>The phone's Ctrl key: on, the next key typed is sent with Ctrl (then it turns off).</summary>
    [ObservableProperty] private bool _ctrlArmed;

    /// <summary>A question is on screen. The terminal steps aside meanwhile: on Android it is a native view that would cover the dialog.</summary>
    [ObservableProperty] private bool _isAskingUser;

    // The server editor (settings page).
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string? _editingId;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editAddress = "";
    [ObservableProperty] private int _editAuthIndex;
    [ObservableProperty] private string? _editError;
    [ObservableProperty] private string _editKeyFile = "";
    [ObservableProperty] private string _editMenuPath = "";

    [ObservableProperty] private VaultSecretRef? _editVaultRef;
    [ObservableProperty] private bool _canOpenVault;

    // This device's key.
    [ObservableProperty] private string _publicKey = "";
    [ObservableProperty] private string _keyFingerprint = "";

    /// <param name="vault">Vault, when Helm has it: servers may sign in with a password or key kept there.</param>
    public SshViewModel(ISettingsStoreFactory settings, SshDeviceKey deviceKey, IUiDispatcher ui, IDialogService dialogs, IClipboardService clipboard,
        ILogger<SshViewModel> logger, IVaultSecrets? vault = null)
    {
        _vault = vault;
        _settings = settings.Get<SshSettings>(SshIds.ModuleId);
        _deviceKey = deviceKey;
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _logger = logger;
        _deviceKey.Changed += (_, _) => _ui.Post(RefreshKey);
        Menu = new SshMenuViewModel(AskAsync, Send);
        Load();
    }

    public ObservableCollection<SshHostRow> Hosts { get; } = [];

    /// <summary>The menu of the server shown (see <see cref="SshMenu"/>).</summary>
    public SshMenuViewModel Menu { get; }


    public ObservableCollection<SshKnownHostRow> KnownHosts { get; } = [];

    public IReadOnlyList<int> FontSizes { get; } = Enumerable.Range(SshSettings.MinFontSize, SshSettings.MaxFontSize - SshSettings.MinFontSize + 1).ToList();

    public IReadOnlyList<string> AuthNames { get; } = ["This device's key", "Password", "A key file on this device", "A password or key in Vault"];

    /// <summary>
    /// The ways to sign in this platform offers, in <see cref="AuthNames"/> order. A key file needs a file system the
    /// user manages (Windows); on Android the key comes from Vault instead.
    /// </summary>
    public IReadOnlyList<SshAuthChoice> AuthChoices => _authChoices ??=
        AuthNames.Select((name, i) => new SshAuthChoice((SshAuthKind)i, name)).Where(c => AllowKeyFiles || c.Kind != SshAuthKind.KeyFile).ToList();

    private IReadOnlyList<SshAuthChoice>? _authChoices;

    /// <summary>False on Android, which leaves the key file choice out. Set by the platform before the pages bind.</summary>
    public bool AllowKeyFiles { get; set; } = true;

    /// <summary>The editor's choice as a <see cref="SshAuthChoice"/> (Android's picker); the same as <see cref="EditAuthIndex"/>.</summary>
    public SshAuthChoice? EditAuthChoice
    {
        get => AuthChoices.FirstOrDefault(c => (int)c.Kind == EditAuthIndex);
        set
        {
            if (value is not null) EditAuthIndex = (int)value.Kind;
        }
    }

    /// <summary>The fields of Vault items the editor offers (names only; values are read when connecting).</summary>
    public ObservableCollection<VaultSecretRef> VaultFields { get; } = [];

    /// <summary>The editor signs in with a Vault field.</summary>
    public bool IsVaultAuth => EditAuthIndex == (int)SshAuthKind.Vault;

    /// <summary>The editor needs Vault unlocked to list its fields.</summary>
    public bool IsVaultLocked => _vault is not null && !_vault.IsUnlocked;

    /// <summary>The editor asks for a key file's path.</summary>
    public bool IsKeyFileAuth => EditAuthIndex == (int)SshAuthKind.KeyFile;

    /// <summary>What the secret box on the page asks for.</summary>
    public string PasswordPrompt => SelectedHost?.Host.Auth is SshAuthKind.KeyFile or SshAuthKind.Vault ? "Key passphrase" : "Password";

    public bool HasHosts => Hosts.Count > 0;

    public bool HasKnownHosts => KnownHosts.Count > 0;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasKey => PublicKey.Length > 0;

    /// <summary>The selected server signs in with a password and is not connected: the page shows the password box.</summary>
    public bool NeedsPassword => !IsConnected && SelectedHost is { } row
                                 && (row.Host.Auth == SshAuthKind.Password
                                     || row.Host.Auth is SshAuthKind.KeyFile or SshAuthKind.Vault && _passphraseNeeded.Contains(row.Id));

    public bool CanConnect => _enabled && SelectedHost is not null && !IsConnecting && !IsConnected;

    /// <summary>Signed in some other way than this device's key (a password, Vault, a key file): the key can be added.</summary>
    public bool CanInstallKey => IsConnected && SelectedHost is { Host.Auth: not SshAuthKind.DeviceKey };

    public string EditorTitle => EditingId is null ? "Add a server" : "Edit server";

    /// <summary>Raised when the page should show the terminal and give it the focus (after connecting).</summary>
    public event EventHandler? TerminalFocusRequested;

    /// <summary>The selected server has a session (open, or ended with its output still on screen).</summary>
    public bool HasSession => ActiveSession is not null;

    partial void OnActiveSessionChanged(SshSession? value)
    {
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(ShowTerminal));
        AttachMenu();
    }

    partial void OnIsAskingUserChanged(bool value) => OnPropertyChanged(nameof(ShowTerminal));

    /// <summary>The terminal is shown: there is a session and no question on screen.</summary>
    public bool ShowTerminal => HasSession && !IsAskingUser;

    /// <summary>Asks a yes/no question with the terminal out of the way (see <see cref="IsAskingUser"/>).</summary>
    public async Task<bool> AskAsync(string title, string message, string confirmText)
    {
        IsAskingUser = true;
        try
        {
            return await _dialogs.ConfirmAsync(title, message, confirmText).ConfigureAwait(true);
        }
        finally
        {
            IsAskingUser = false;
        }
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnPublicKeyChanged(string value) => OnPropertyChanged(nameof(HasKey));

    partial void OnEditingIdChanged(string? value) => OnPropertyChanged(nameof(EditorTitle));

    partial void OnEditAuthIndexChanged(int value)
    {
        OnPropertyChanged(nameof(EditAuthChoice));
        OnPropertyChanged(nameof(IsKeyFileAuth));
        OnPropertyChanged(nameof(IsVaultAuth));
        if (IsVaultAuth) RefreshVaultFields();
    }

    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    partial void OnIsConnectedChanged(bool value)
    {
        AttachMenu();
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(NeedsPassword));
        OnPropertyChanged(nameof(CanInstallKey));
    }

    partial void OnSelectedHostChanged(SshHostRow? value)
    {
        OnPropertyChanged(nameof(NeedsPassword));
        OnPropertyChanged(nameof(PasswordPrompt));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanInstallKey));
        ActiveSession = value is not null && _sessions.TryGetValue(value.Id, out var session) ? session : null;
        RefreshSession();
        if (!_loading) _settings.Update(s => s.SelectedHostId = value?.Id);
    }

    partial void OnFontSizeChanged(int value)
    {
        if (!_loading && value > 0) _settings.Update(s => s.FontSize = Math.Clamp(value, SshSettings.MinFontSize, SshSettings.MaxFontSize));
    }

    /// <summary>The module turned on or off. Off closes every session; the list stays.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) CloseAll();
        OnPropertyChanged(nameof(CanConnect));
    }

    /// <summary>The terminal's size in characters, from the page; the active session follows it.</summary>
    public void ReportTerminalSize(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        ActiveSession?.Resize(columns, rows);
    }

    /// <summary>Keys or a paste from the terminal. With <see cref="CtrlArmed"/>, one typed key goes with Ctrl.</summary>
    public void Send(string text)
    {
        if (!_enabled) return;
        if (CtrlArmed && text.Length == 1)
        {
            CtrlArmed = false;
            text = TerminalKeys.Ctrl(text[0]) ?? text;
        }
        ActiveSession?.Send(text);
    }

    /// <summary>Connects to the selected server. <paramref name="password"/> is used for this connection only.</summary>
    public async Task ConnectAsync(string? password)
    {
        if (!CanConnect || SelectedHost is not { } row) return;
        var host = row.Host;
        if (host.Auth == SshAuthKind.Password && string.IsNullOrEmpty(password))
        {
            Message = "Type the password for " + host.Target + " first.";
            return;
        }
        Message = null;
        CanOpenVault = false;
        string? vaultSecret = null;
        if (host.Auth == SshAuthKind.Vault)
        {
            vaultSecret = await ReadVaultSecretAsync(host).ConfigureAwait(true);
            if (vaultSecret is null) return;
        }
        if (_sessions.Remove(host.Id, out var old)) old.Dispose();
        var session = new SshSession(host, _columns, _rows);
        session.StateChanged += (_, _) => _ui.Post(RefreshSession);
        _sessions[host.Id] = session;
        if (SelectedHost == row) ActiveSession = session;
        IsConnecting = true;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Renci.SshNet.ConnectionInfo info;
                try
                {
                    info = SshConnection.Create(host, _deviceKey, password, vaultSecret);
                }
                catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException
                                               or Org.BouncyCastle.Crypto.CryptoException or InvalidOperationException or ArgumentException)
                {
                    Message = KeyProblem(host, ex, password);
                    _sessions.Remove(host.Id);
                    session.Dispose();
                    if (ActiveSession == session) ActiveSession = null;
                    OnPropertyChanged(nameof(NeedsPassword));
                    return;
                }
                if (await session.ConnectAsync(info, key => SshKnownHosts.Check(_settings.Current.KnownHosts, key)).ConfigureAwait(true)) break;
                if (session.RejectedHostKey is not { } key) break;
                if (session.RejectedMatch == HostKeyMatch.Changed)
                {
                    // Never offered as a click-through: a changed key is what an attack in the middle looks like.
                    Message = $"The key of {key.Address} has changed since you trusted it, so Helm did not connect. Someone may be "
                              + $"intercepting the connection. If the server was reinstalled, check its new key ({key.DisplayFingerprint}) "
                              + "on the server, then forget the old one in SSH settings → Known servers and connect again.";
                    break;
                }
                if (attempt > 0 || !await AskTrustAsync(key).ConfigureAwait(true))
                {
                    Message = "Not connected: the server's key was not trusted.";
                    break;
                }
                _settings.Update(s => s.KnownHosts = SshKnownHosts.Trust(s.KnownHosts, key, DateTimeOffset.Now));
                RefreshKnownHosts();
            }
            if (session.IsConnected) TerminalFocusRequested?.Invoke(this, EventArgs.Empty);
            else
            {
                if (session.Error is { } error && Message is null) Message = error;
                // Nothing to show: the page goes back to its "choose a server" note instead of an empty terminal.
                if (!session.HasOutput && _sessions.GetValueOrDefault(host.Id) == session)
                {
                    _sessions.Remove(host.Id);
                    session.Dispose();
                    if (ActiveSession == session) ActiveSession = null;
                }
            }
        }
        finally
        {
            IsConnecting = false;
            RefreshSession();
        }
    }

    /// <summary>
    /// The server's password or key from Vault. A locked vault is opened with Windows Hello or the fingerprint when this
    /// device allows it; otherwise the page offers to open Vault. Null (with a message) when it cannot be read.
    /// </summary>
    private async Task<string?> ReadVaultSecretAsync(SshHost host)
    {
        if (_vault is null)
        {
            Message = "Vault is not part of this Helm, so this server cannot sign in. Edit it to sign in another way.";
            return null;
        }
        if (!_vault.IsUnlocked && !await _vault.TryQuickUnlockAsync().ConfigureAwait(true))
        {
            Message = $"Unlock Vault first: the {host.VaultField} for {host.DisplayName} is kept there.";
            CanOpenVault = true;
            return null;
        }
        OnPropertyChanged(nameof(IsVaultLocked));
        var secret = _vault.Read(host.VaultItemUid ?? "", host.VaultField ?? "");
        if (string.IsNullOrEmpty(secret))
        {
            Message = $"Vault has no field {host.VaultField} in {host.VaultItemTitle} any more. Edit the server to choose it again.";
            return null;
        }
        return secret;
    }

    [RelayCommand]
    private void OpenVault()
    {
        CanOpenVault = false;
        _vault?.ShowVault();
    }

    /// <summary>The editor's "Unlock Vault": quick unlock when possible, else Vault opens for the password.</summary>
    [RelayCommand]
    private async Task UnlockVaultAsync()
    {
        if (_vault is null) return;
        if (!await _vault.TryQuickUnlockAsync().ConfigureAwait(true)) _vault.ShowVault();
        RefreshVaultFields();
    }

    /// <summary>The settings page is shown again (e.g. after unlocking Vault there): the editor's Vault list follows.</summary>
    public void OnSettingsShown()
    {
        if (IsEditing && IsVaultAuth) RefreshVaultFields();
    }

    private void RefreshVaultFields()
    {
        var keep = EditVaultRef;
        VaultFields.Clear();
        if (_vault is not null)
            foreach (var field in _vault.ListFields()) VaultFields.Add(field);
        // While locked, the field already chosen stays shown (by name) so saving does not lose it.
        if (keep is not null && !VaultFields.Any(f => f.ItemUid == keep.ItemUid && f.FieldName == keep.FieldName)) VaultFields.Insert(0, keep);
        EditVaultRef = keep is null ? null : VaultFields.First(f => f.ItemUid == keep.ItemUid && f.FieldName == keep.FieldName);
        OnPropertyChanged(nameof(IsVaultLocked));
    }

    /// <summary>The key's name in messages: its file, or where it is in Vault.</summary>
    private static string KeyName(SshHost host) =>
        host.Auth == SshAuthKind.Vault ? $"in Vault ({host.VaultItemTitle} · {host.VaultField})" : Path.GetFileName(host.KeyFile) ?? "";

    /// <summary>Why a key could not be used, in words; a key that needs a passphrase makes the page ask for it.</summary>
    private string KeyProblem(SshHost host, Exception ex, string? passphrase)
    {
        switch (ex)
        {
            // An encrypted key opened without a passphrase: SSH.NET says so for OpenSSH keys, PKCS#8 keys fail to decrypt.
            case Renci.SshNet.Common.SshPassPhraseNullOrEmptyException:
            case Org.BouncyCastle.Crypto.CryptoException when string.IsNullOrEmpty(passphrase):
                _passphraseNeeded.Add(host.Id);
                return $"The key {KeyName(host)} is protected by a passphrase. Type it in the box and connect again.";
            case FileNotFoundException:
                return $"The key file {host.KeyFile} was not found. Edit the server in SSH settings to choose another.";
            case UnauthorizedAccessException:
                return $"Helm may not read the key file {host.KeyFile}.";
            case Renci.SshNet.Common.SshException or Org.BouncyCastle.Crypto.CryptoException when host.Auth is SshAuthKind.KeyFile or SshAuthKind.Vault && !string.IsNullOrEmpty(passphrase):
                _passphraseNeeded.Add(host.Id);
                return $"The key {KeyName(host)} could not be opened: the passphrase may be wrong.";
            case Renci.SshNet.Common.SshException when host.Auth is SshAuthKind.KeyFile or SshAuthKind.Vault:
                return $"The key {KeyName(host)} could not be read ({ex.Message}). PuTTY .ppk keys need converting to OpenSSH format first.";
            default:
                return ex.Message;
        }
    }

    private Task<bool> AskTrustAsync(SshHostKey key) =>
        AskAsync("Trust this server?",
            $"This is the first connection to {key.Address} from this device. The server's {key.Algorithm} key is:\n\n{key.DisplayFingerprint}\n\n"
            + $"Trust it only if it matches what the server shows for: ssh-keygen -lf {SshKnownHosts.ServerKeyFile(key.Algorithm)}",
            "Trust and connect");

    [RelayCommand]
    private void Disconnect()
    {
        if (SelectedHost is { } row && _sessions.TryGetValue(row.Id, out var session)) session.Close();
        RefreshSession();
    }

    [RelayCommand]
    private void DismissMessage()
    {
        Message = null;
        CanOpenVault = false;
    }

    /// <summary>Adds this device's key to the connected server (signed in with a password), then uses the key from now on.</summary>
    [RelayCommand]
    private async Task InstallKeyAsync()
    {
        if (!CanInstallKey || SelectedHost is not { } row || ActiveSession is not { } session) return;
        try
        {
            _deviceKey.EnsureCreated();
            var (exit, output) = await session.RunCommandAsync(SshKeyInstall.Command(_deviceKey.PublicKeyLine), TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (exit != 0)
            {
                Message = $"The key could not be added (exit code {exit}). {output}".Trim();
                return;
            }
            SaveHost(row.Host with { Auth = SshAuthKind.DeviceKey, KeyFile = null, VaultItemUid = null, VaultItemTitle = null, VaultField = null });
            Message = $"This device's key is now on {row.Host.Target}. Next time Helm signs in with it, without a password.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Installing the SSH key failed: {Type}", ex.GetType().Name);
            Message = "The key could not be added: " + ex.Message;
        }
    }

    [RelayCommand]
    private void CopyPublicKey()
    {
        try
        {
            _deviceKey.EnsureCreated();
            _clipboard.SetText(_deviceKey.PublicKeyLine);
            Message = "Public key copied. Add it as one line to ~/.ssh/authorized_keys on the server.";
        }
        catch (CryptographicException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private async Task NewKeyAsync()
    {
        if (!await AskAsync("Make a new key for this device?",
                "Servers that have the current key will refuse this device until you add the new one. The old key is deleted from this device.",
                "Make new key").ConfigureAwait(true)) return;
        try
        {
            _deviceKey.Regenerate();
            Message = "New key made. Add it to your servers before you sign in with it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning("Making a new SSH key failed: {Type}", ex.GetType().Name);
            Message = "The new key could not be saved: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ForgetKnownHostAsync(SshKnownHostRow? row)
    {
        if (row is null) return;
        if (!await AskAsync($"Forget the key of {row.Title}?",
                "The next connection will show the server's key again and ask you to trust it. Do this only if you know the server's key changed.",
                "Forget").ConfigureAwait(true)) return;
        _settings.Update(s => s.KnownHosts = s.KnownHosts.Where(k => k != row.Known).ToList());
        RefreshKnownHosts();
    }

    /// <summary>
    /// Adds the servers of this device's OpenSSH setup (<paramref name="sshFolder"/>, usually ~/.ssh): each Host of
    /// its config with its key file, and the server keys its known_hosts already trusts, so they connect without asking.
    /// Servers already in the list are skipped. Private keys stay where they are; only their paths are kept.
    /// </summary>
    public void ImportOpenSsh(string sshFolder)
    {
        try
        {
            var config = Path.Combine(sshFolder, "config");
            if (!File.Exists(config))
            {
                Message = $"No SSH config was found in {sshFolder}. Add the server by hand instead.";
                return;
            }
            var home = Path.GetDirectoryName(Path.GetFullPath(sshFolder).TrimEnd(Path.DirectorySeparatorChar)) ?? sshFolder;
            var knownHostsFile = Path.Combine(sshFolder, "known_hosts");
            var knownHosts = File.Exists(knownHostsFile) ? File.ReadAllText(knownHostsFile) : "";
            var added = new List<string>();
            var trusted = 0;
            SshHostRow? first = null;
            foreach (var entry in SshOpenSsh.ParseConfig(File.ReadAllText(config), home))
            {
                var keyFile = SshOpenSsh.KeyFileFor(entry, sshFolder);
                var existing = Hosts.FirstOrDefault(h => h.Host.Port == entry.Port && h.Host.User == entry.User
                                                         && string.Equals(h.Host.Address, entry.Address, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    var host = new SshHost
                    {
                        Name = entry.Alias, Address = entry.Address, Port = entry.Port, User = entry.User,
                        Auth = keyFile is null ? SshAuthKind.Password : SshAuthKind.KeyFile, KeyFile = keyFile,
                    };
                    SaveHost(host);
                    added.Add(entry.Alias);
                    first ??= Hosts.FirstOrDefault(h => h.Id == host.Id);
                }
                foreach (var key in SshOpenSsh.KnownKeys(knownHosts, entry.Address, entry.Port))
                {
                    if (SshKnownHosts.Check(_settings.Current.KnownHosts, key) != HostKeyMatch.Unknown) continue;
                    _settings.Update(s => s.KnownHosts = SshKnownHosts.Trust(s.KnownHosts, key, DateTimeOffset.Now));
                    trusted++;
                }
            }
            RefreshKnownHosts();
            if (first is not null) SelectedHost = first;
            Message = added.Count == 0
                ? "Nothing new to import: the servers in your SSH config are already here."
                : $"Imported {string.Join(", ", added)} from your SSH config"
                  + (trusted > 0 ? $", with {trusted} server key{(trusted == 1 ? "" : "s")} you already trusted there." : ".")
                  + " Choose it and connect.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Importing the SSH config failed: {Type}", ex.GetType().Name);
            Message = "Your SSH config could not be read: " + ex.Message;
        }
    }

    // ---- Server editor -------------------------------------------------------------------------------------------

    [RelayCommand]
    private void NewHost()
    {
        EditingId = null;
        EditName = "";
        EditAddress = "";
        EditAuthIndex = 0;
        EditKeyFile = "";
        EditVaultRef = null;
        EditMenuPath = "";
        EditError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditHost(SshHostRow? row)
    {
        if (row is null) return;
        EditingId = row.Id;
        EditName = row.Host.Name;
        EditAddress = row.Host.Target;
        EditAuthIndex = (int)row.Host.Auth;
        EditKeyFile = row.Host.KeyFile ?? "";
        EditMenuPath = row.Host.MenuPath ?? "";
        EditVaultRef = row.Host is { VaultItemUid: { } uid, VaultField: { } field } ? new VaultSecretRef(uid, row.Host.VaultItemTitle ?? "", field) : null;
        if (row.Host.Auth == SshAuthKind.Vault) RefreshVaultFields();
        EditError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditError = null;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (!SshAddress.TryParse(EditAddress, out var user, out var address, out var port))
        {
            EditError = "Write the address as user@host, or user@host:port for another port than 22.";
            return;
        }
        var auth = Enum.IsDefined((SshAuthKind)EditAuthIndex) ? (SshAuthKind)EditAuthIndex : SshAuthKind.DeviceKey;
        var keyFile = EditKeyFile.Trim().Trim('"');
        if (auth == SshAuthKind.KeyFile && (keyFile.Length == 0 || !Path.IsPathFullyQualified(keyFile) || !File.Exists(keyFile)))
        {
            EditError = "Choose the private key file, for example the id_ed25519 or id_rsa file in your .ssh folder.";
            return;
        }
        if (auth == SshAuthKind.Vault && EditVaultRef is null)
        {
            EditError = IsVaultLocked ? "Unlock Vault, then choose the item and field that hold the password or key." : "Choose the Vault item and field that hold the password or key.";
            return;
        }
        var menuPath = EditMenuPath.Trim();
        if (menuPath.Length > 0 && !SshMenu.IsValidPath(menuPath))
        {
            EditError = "Write the menu path as a plain path, e.g. ~/.helm/menu (letters, digits, . _ - and /).";
            return;
        }
        var vaultRef = auth == SshAuthKind.Vault ? EditVaultRef : null;
        var existing = EditingId is null ? null : _settings.Current.Hosts.FirstOrDefault(h => h.Id == EditingId);
        var host = (existing ?? new SshHost()) with
        {
            Name = EditName.Trim(), User = user, Address = address, Port = port, Auth = auth,
            KeyFile = auth == SshAuthKind.KeyFile ? keyFile : null,
            VaultItemUid = vaultRef?.ItemUid, VaultItemTitle = vaultRef?.ItemTitle, VaultField = vaultRef?.FieldName,
            MenuPath = menuPath.Length == 0 || menuPath == SshMenu.DefaultPath ? null : menuPath,
        };
        _passphraseNeeded.Remove(host.Id);
        SaveHost(host);
        IsEditing = false;
        EditError = null;
        SelectedHost = Hosts.FirstOrDefault(h => h.Id == host.Id);
    }

    [RelayCommand]
    private async Task DeleteHostAsync(SshHostRow? row)
    {
        if (row is null) return;
        if (!await AskAsync($"Delete {row.Title}?", "The server is removed from the list and its session is closed. Its known key is kept.", "Delete")
                .ConfigureAwait(true)) return;
        if (_sessions.Remove(row.Id, out var session)) session.Dispose();
        _settings.Update(s => s.Hosts = s.Hosts.Where(h => h.Id != row.Id).ToList());
        Hosts.Remove(row);
        if (SelectedHost == row) SelectedHost = Hosts.FirstOrDefault();
        if (EditingId == row.Id) CancelEdit();
        OnPropertyChanged(nameof(HasHosts));
    }

    private void SaveHost(SshHost host)
    {
        _settings.Update(s =>
        {
            var index = s.Hosts.FindIndex(h => h.Id == host.Id);
            if (index >= 0) s.Hosts[index] = host;
            else s.Hosts.Add(host);
        });
        if (Hosts.FirstOrDefault(h => h.Id == host.Id) is { } row)
        {
            row.Host = host;
            OnPropertyChanged(nameof(NeedsPassword));
            OnPropertyChanged(nameof(PasswordPrompt));
            OnPropertyChanged(nameof(CanInstallKey));
        }
        else Hosts.Add(new SshHostRow(host));
        OnPropertyChanged(nameof(HasHosts));
    }

    // ---- State -----------------------------------------------------------------------------------------------------

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            foreach (var host in s.Hosts) Hosts.Add(new SshHostRow(host));
            FontSize = Math.Clamp(s.FontSize, SshSettings.MinFontSize, SshSettings.MaxFontSize);
            SelectedHost = Hosts.FirstOrDefault(h => h.Id == s.SelectedHostId) ?? Hosts.FirstOrDefault();
            RefreshKnownHosts();
            RefreshKey();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshKey()
    {
        PublicKey = _deviceKey.PublicKeyLine;
        KeyFingerprint = _deviceKey.Fingerprint;
    }

    private void RefreshKnownHosts()
    {
        KnownHosts.Clear();
        foreach (var known in _settings.Current.KnownHosts.OrderBy(k => k.Address, StringComparer.OrdinalIgnoreCase)) KnownHosts.Add(new SshKnownHostRow(known));
        OnPropertyChanged(nameof(HasKnownHosts));
    }

    private void RefreshSession()
    {
        foreach (var row in Hosts)
        {
            var session = _sessions.GetValueOrDefault(row.Id);
            row.IsConnected = session?.IsConnected == true;
            row.State = session?.State switch
            {
                SshSessionState.Connected => "Connected",
                SshSessionState.Connecting => "Connecting…",
                _ => "",
            };
        }
        var active = ActiveSession;
        IsConnected = active?.IsConnected == true;
        SessionStatus = active?.State switch
        {
            SshSessionState.Connecting => "Connecting…",
            SshSessionState.Connected => "Connected" + (active.ServerVersion is { Length: > 0 } v ? " · " + v.Replace("SSH-2.0-", "", StringComparison.Ordinal) : ""),
            SshSessionState.Closed => "Disconnected",
            SshSessionState.Failed => "Not connected",
            _ => SelectedHost is null ? "" : "Not connected",
        };
    }

    /// <summary>The menu follows the session shown, while it is connected.</summary>
    private void AttachMenu()
    {
        var session = ActiveSession is { IsConnected: true } s ? s : null;
        Menu.Attach(session, session?.Host.MenuPath, session?.Host.DisplayName ?? "");
    }

    private void CloseAll()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        ActiveSession = null;
        RefreshSession();
    }

    public void Dispose() => CloseAll();
}
