using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.Workspace;

/// <summary>
/// Workspace Controller, PC side: the host and its management screen (what is connected); the phone is the remote
/// control. It sees every Claude Code session in VS Code through Claude's hooks
/// and types prompts into them through Helm Bridge; you follow and steer them from the phone. While on, it listens on
/// 127.0.0.1 and on a named pipe, and shows under General → Running in background.
/// </summary>
public sealed class WorkspaceModule : HelmModuleBase, IModuleContent
{
    private readonly HelmPaths _paths;
    private readonly PromptSender _sender;
    private readonly BridgeInstaller _installer;
    private readonly IBackgroundActivities _activities;
    private readonly ILogger<WorkspaceModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISyncedCollection<WorkspaceHostRecord> _hosts;
    private readonly ISyncedCollection<WorkspaceSessionRecord> _sessions;
    private readonly ISyncedCollection<WorkspaceCommand> _commands;
    private readonly ISyncedCollection<WorkspaceResultRecord> _results;
    private readonly ISyncedCollection<WorkspaceHistoryRecord>? _history;
    private readonly IDeviceInfo? _device;
    private readonly ClaudeCatalog _catalog;
    private readonly IUserActivity? _userActivity;
    private WorkspaceEngine? _engine;
    private WorkspaceRelay? _relay;
    private BackgroundActivity? _activity;
    private string? _hooksProblem;
    private string? _bridgeProblem;

    public WorkspaceModule(ISettingsStoreFactory settings, HelmPaths paths, PromptSender sender, BridgeInstaller installer,
        IBackgroundActivities activities, ISyncedCollection<WorkspaceHostRecord> hosts, ISyncedCollection<WorkspaceSessionRecord> sessions,
        ISyncedCollection<WorkspaceCommand> commands, ISyncedCollection<WorkspaceResultRecord> results, ClaudeCatalog catalog,
        ILogger<WorkspaceModule> logger, IDeviceInfo? device = null, IUserActivity? activity = null, ISyncedCollection<WorkspaceHistoryRecord>? history = null)
    {
        _history = history;
        Settings = settings.Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _paths = paths;
        _sender = sender;
        _installer = installer;
        _activities = activities;
        _hosts = hosts;
        _sessions = sessions;
        _commands = commands;
        _results = results;
        _device = device;
        _catalog = catalog;
        _userActivity = activity;
        catalog.Updated += (_, _) =>
        {
            if (_engine is { } engine && catalog.Models.Count > 0) engine.AutoModeAvailable = catalog.AutoModeAvailable;
            _relay?.RequestPublish();
        };
        _logger = logger;
    }

    public override string Id => WorkspaceIds.ModuleId;
    public override string DisplayName => "Workspace Controller";
    public override string Description => "Lets your phone follow your Claude Code sessions in VS Code on this PC and give them their next task.";
    public override ModuleGroup Group => ModuleGroup.WindowsAndDesktop;
    public override SymbolRegular Icon => SymbolRegular.PhoneLaptop24;
    public override System.Windows.Media.ImageSource IconImage => WorkspaceLogo.Image;
    public override Type SettingsPageType => typeof(WorkspacePage);
    public Type ContentPageType => typeof(WorkspaceContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public ISettingsStore<WorkspaceSettings> Settings { get; }

    /// <summary>Null while the tool is off.</summary>
    public WorkspaceEngine? Engine => _engine;

    /// <summary>The engine started or stopped, or its sessions and windows changed. Off the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Why Helm's hooks are not in Claude's settings; null when they are (or Helm does not manage them).</summary>
    public string? HooksProblem => _hooksProblem;

    /// <summary>Why Helm Bridge is not installed in VS Code; null when it is (or Helm does not manage it).</summary>
    public string? BridgeProblem => _bridgeProblem;

    public override async Task EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null) return;
            StatusMessage = null;
            try
            {
                _engine = WorkspaceEngine.Start(Settings, _paths, _sender, _logger, activity: _userActivity,
                    bridgePipe: Environment.GetEnvironmentVariable("HELM_BRIDGE_PIPE"));
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
            {
                _logger.LogError(ex, "Workspace Controller could not start");
                StatusMessage = "Workspace Controller could not start listening: " + ex.Message;
                throw;
            }
            _engine.Changed += OnEngineChanged;
            if (Settings.Current.ManageClaudeHooks) _hooksProblem = _engine.InstallClaudeHooks(HooksFileOverride());
            // The phone's link: this PC and its connected sessions out, the phone's commands in (Helm Sync).
            _relay = new WorkspaceRelay(_engine, _hosts, _sessions, _commands, _results, _device?.DeviceName ?? Environment.MachineName,
                () => _catalog.Current, _logger, models: () => (_catalog.Models, _catalog.AutoModeAvailable), history: _history);
            if (_catalog.Models.Count > 0) _engine.AutoModeAvailable = _catalog.AutoModeAvailable;
            _engine.PhoneLinked = true;
            _activity = _activities.Start(Id, DisplayName, Detail(), StopFromGeneralAsync);
            UpdateStatus();
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (Settings.Current.ManageBridgeExtension) _ = EnsureBridgeAsync();
    }

    public override async Task DisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _activity?.Dispose();
            _activity = null;
            if (_relay is not null) await _relay.DisposeAsync().ConfigureAwait(false);
            _relay = null;
            if (_engine is null) return;
            _engine.Changed -= OnEngineChanged;
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes Helm's hooks into Claude Code's settings now (Settings page button).</summary>
    public void InstallHooks()
    {
        if (_engine is null) return;
        _hooksProblem = _engine.InstallClaudeHooks(HooksFileOverride());
        UpdateStatus();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes Helm's hooks out of Claude Code's settings (Settings page button, or managing turned off).</summary>
    public void RemoveHooks()
    {
        _hooksProblem = WorkspaceEngine.RemoveClaudeHooks(_logger, HooksFileOverride());
        UpdateStatus();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Installs Helm Bridge into VS Code if this version is not there yet.</summary>
    public async Task EnsureBridgeAsync()
    {
        try
        {
            _bridgeProblem = await _installer.EnsureInstalledAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Helm Bridge install failed");
            _bridgeProblem = "Helm Bridge could not be installed: " + ex.Message;
        }
        UpdateStatus();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Development and tests: hooks go into this file instead of the user's Claude settings.</summary>
    private static string? HooksFileOverride() => Environment.GetEnvironmentVariable("HELM_CLAUDE_SETTINGS");

    private Task StopFromGeneralAsync()
    {
        // Turning the tool off goes through the module registry, which stops it and remembers it is off.
        IsEnabled = false;
        return Task.CompletedTask;
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        // The "/" list of the phone: what Claude Code offers in each connected folder.
        if (_engine is { } engine) _catalog.Refresh(engine.Windows().SelectMany(w => w.Connection.Info.Folders));
        if (_activity is not null) _activity.Detail = Detail();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string Detail()
    {
        if (_engine is null) return "Stopped";
        var windows = _engine.Windows().Count;
        var working = _engine.Registry.Sessions.Count(s => s.State == SessionState.Working);
        var text = windows == 1 ? "1 VS Code window" : $"{windows} VS Code windows";
        return working > 0 ? $"{text} · Claude working in {working}" : text;
    }

    private void UpdateStatus() => StatusMessage = _hooksProblem ?? _bridgeProblem;
}
