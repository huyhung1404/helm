using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public string AuthText => Host.Auth == SshAuthKind.DeviceKey ? "This device's key" : "Password";

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
    private readonly Dictionary<string, SshSession> _sessions = new(StringComparer.Ordinal);
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

    // The server editor (settings page).
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string? _editingId;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editAddress = "";
    [ObservableProperty] private int _editAuthIndex;
    [ObservableProperty] private string? _editError;

    // This device's key.
    [ObservableProperty] private string _publicKey = "";
    [ObservableProperty] private string _keyFingerprint = "";

    public SshViewModel(ISettingsStoreFactory settings, SshDeviceKey deviceKey, IUiDispatcher ui, IDialogService dialogs, IClipboardService clipboard,
        ILogger<SshViewModel> logger)
    {
        _settings = settings.Get<SshSettings>(SshIds.ModuleId);
        _deviceKey = deviceKey;
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _logger = logger;
        _deviceKey.Changed += (_, _) => _ui.Post(RefreshKey);
        Load();
    }

    public ObservableCollection<SshHostRow> Hosts { get; } = [];

    public ObservableCollection<SshKnownHostRow> KnownHosts { get; } = [];

    public IReadOnlyList<int> FontSizes { get; } = Enumerable.Range(SshSettings.MinFontSize, SshSettings.MaxFontSize - SshSettings.MinFontSize + 1).ToList();

    public IReadOnlyList<string> AuthNames { get; } = ["This device's key", "Password"];

    public bool HasHosts => Hosts.Count > 0;

    public bool HasKnownHosts => KnownHosts.Count > 0;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasKey => PublicKey.Length > 0;

    /// <summary>The selected server signs in with a password and is not connected: the page shows the password box.</summary>
    public bool NeedsPassword => SelectedHost?.Host.Auth == SshAuthKind.Password && !IsConnected;

    public bool CanConnect => _enabled && SelectedHost is not null && !IsConnecting && !IsConnected;

    public bool CanInstallKey => IsConnected && SelectedHost?.Host.Auth == SshAuthKind.Password;

    public string EditorTitle => EditingId is null ? "Add a server" : "Edit server";

    /// <summary>Raised when the page should show the terminal and give it the focus (after connecting).</summary>
    public event EventHandler? TerminalFocusRequested;

    /// <summary>The selected server has a session (open, or ended with its output still on screen).</summary>
    public bool HasSession => ActiveSession is not null;

    partial void OnActiveSessionChanged(SshSession? value) => OnPropertyChanged(nameof(HasSession));

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnPublicKeyChanged(string value) => OnPropertyChanged(nameof(HasKey));

    partial void OnEditingIdChanged(string? value) => OnPropertyChanged(nameof(EditorTitle));

    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(NeedsPassword));
        OnPropertyChanged(nameof(CanInstallKey));
    }

    partial void OnSelectedHostChanged(SshHostRow? value)
    {
        OnPropertyChanged(nameof(NeedsPassword));
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

    /// <summary>Keys or a paste from the terminal.</summary>
    public void Send(string text)
    {
        if (_enabled) ActiveSession?.Send(text);
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
                    info = SshConnection.Create(host, _deviceKey, password);
                }
                catch (CryptographicException ex)
                {
                    Message = ex.Message;
                    session.Close();
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

    private Task<bool> AskTrustAsync(SshHostKey key) =>
        _dialogs.ConfirmAsync("Trust this server?",
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
    private void DismissMessage() => Message = null;

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
            SaveHost(row.Host with { Auth = SshAuthKind.DeviceKey });
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
        if (!await _dialogs.ConfirmAsync("Make a new key for this device?",
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
        if (!await _dialogs.ConfirmAsync($"Forget the key of {row.Title}?",
                "The next connection will show the server's key again and ask you to trust it. Do this only if you know the server's key changed.",
                "Forget").ConfigureAwait(true)) return;
        _settings.Update(s => s.KnownHosts = s.KnownHosts.Where(k => k != row.Known).ToList());
        RefreshKnownHosts();
    }

    // ---- Server editor -------------------------------------------------------------------------------------------

    [RelayCommand]
    private void NewHost()
    {
        EditingId = null;
        EditName = "";
        EditAddress = "";
        EditAuthIndex = 0;
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
        var auth = EditAuthIndex == 1 ? SshAuthKind.Password : SshAuthKind.DeviceKey;
        var existing = EditingId is null ? null : _settings.Current.Hosts.FirstOrDefault(h => h.Id == EditingId);
        var host = (existing ?? new SshHost()) with { Name = EditName.Trim(), User = user, Address = address, Port = port, Auth = auth };
        SaveHost(host);
        IsEditing = false;
        EditError = null;
        SelectedHost = Hosts.FirstOrDefault(h => h.Id == host.Id);
    }

    [RelayCommand]
    private async Task DeleteHostAsync(SshHostRow? row)
    {
        if (row is null) return;
        if (!await _dialogs.ConfirmAsync($"Delete {row.Title}?", "The server is removed from the list and its session is closed. Its known key is kept.", "Delete")
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

    private void CloseAll()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        ActiveSession = null;
        RefreshSession();
    }

    public void Dispose() => CloseAll();
}
