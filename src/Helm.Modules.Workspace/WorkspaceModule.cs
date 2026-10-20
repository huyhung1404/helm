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
    private readonly IClaudeStarter? _starter;
    private readonly BlobStore? _blobs;
    private readonly ISyncService? _sync;
    private WorkspaceEngine? _engine;
    private WorkspaceRelay? _relay;
    private WorkspaceImageStore? _images;
    private BackgroundActivity? _activity;
    private string? _hooksProblem;
    private string? _bridgeProblem;
    private ClaudeHooksGuard? _hooksGuard;

    public WorkspaceModule(ISettingsStoreFactory settings, HelmPaths paths, PromptSender sender, BridgeInstaller installer,
        IBackgroundActivities activities, ISyncedCollection<WorkspaceHostRecord> hosts, ISyncedCollection<WorkspaceSessionRecord> sessions,
        ISyncedCollection<WorkspaceCommand> commands, ISyncedCollection<WorkspaceResultRecord> results, ClaudeCatalog catalog,
        ILogger<WorkspaceModule> logger, IDeviceInfo? device = null, IUserActivity? activity = null, ISyncedCollection<WorkspaceHistoryRecord>? history = null,
        IClaudeStarter? starter = null, BlobStore? blobs = null, ISyncService? sync = null)
    {
        _starter = starter;
        _blobs = blobs;
        _sync = sync;
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
                // Sessions outside VS Code: Helm runs them itself when the phone sends them something.
                var headless = _starter is null ? null : new HeadlessClaude(_starter, _logger);
                // Pictures from the phone come as blobs of Helm Sync; without sync there are none.
                var sync = _sync;
                _images = _blobs is null ? null
                    : new WorkspaceImageStore(_blobs, Path.Combine(_paths.ModuleDataDirectory(Id), "images"), _logger, sync is null ? null : () => sync.RequestSync());
                _engine = WorkspaceEngine.Start(Settings, _paths, _sender, _logger, activity: _userActivity,
                    bridgePipe: Environment.GetEnvironmentVariable("HELM_BRIDGE_PIPE"), headless: headless, images: _images);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
            {
                _logger.LogError(ex, "Workspace Controller could not start");
                StatusMessage = "Workspace Controller could not start listening: " + ex.Message;
                throw;
            }
            _engine.Changed += OnEngineChanged;
            StartHooks(_engine);
            // The phone's link: this PC and its connected sessions out, the phone's commands in (Helm Sync).
            _relay = new WorkspaceRelay(_engine, _hosts, _sessions, _commands, _results, _device?.DeviceName ?? Environment.MachineName,
                () => _catalog.Current, _logger, models: () => (_catalog.Models, _catalog.AutoModeAvailable), history: _history, images: _images);
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
            _images?.Dispose();
            _images = null;
            _hooksGuard?.Dispose();
            _hooksGuard = null;
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
        _hooksProblem = HooksFile() is { } file ? _engine.InstallClaudeHooks(file) : TestCopyHooks;
        UpdateStatus();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes Helm's hooks out of Claude Code's settings (Settings page button, or managing turned off).</summary>
    public void RemoveHooks()
    {
        if (HooksFile() is not { } file) return;
        _hooksProblem = WorkspaceEngine.RemoveClaudeHooks(_logger, file);
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

    /// <summary>
    /// The Claude settings file Helm's hooks go into: HELM_CLAUDE_SETTINGS (development, tests), else the user's own,
    /// except for a copy on a test data folder, which would take the installed Helm's place there (null).
    /// </summary>
    private string? HooksFile() => Environment.GetEnvironmentVariable("HELM_CLAUDE_SETTINGS") is { Length: > 0 } file ? file
        : _paths.IsDefault ? ClaudeHookConfig.UserSettingsPath() : null;

    private const string TestCopyHooks = "This copy of Helm runs on a test data folder (--data-dir), so it leaves Claude Code's settings alone. Set HELM_CLAUDE_SETTINGS to a file to try the hooks.";

    /// <summary>Writes the hooks, then keeps them there while the tool runs (another program may take them out).</summary>
    private void StartHooks(WorkspaceEngine engine)
    {
        if (HooksFile() is not { } file)
        {
            _hooksProblem = TestCopyHooks;
            return;
        }
        if (Settings.Current.ManageClaudeHooks) _hooksProblem = engine.InstallClaudeHooks(file);
        _hooksGuard = new ClaudeHooksGuard(file,
            json => !Settings.Current.ManageClaudeHooks || ClaudeHookConfig.IsInstalled(json, engine.HookPort, Settings.Current.HookToken),
            () => engine.InstallClaudeHooks(file), _logger);
        _hooksGuard.Restored += problem =>
        {
            _hooksProblem = problem;
            UpdateStatus();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

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
        if (working > 0) text += $" · Claude working in {working}";
        var hosted = _engine.HostedRuns.Count;
        return hosted > 0 ? text + $" · {hosted} run{(hosted == 1 ? "" : "s")} for the phone" : text;
    }

    private void UpdateStatus() => StatusMessage = _hooksProblem ?? _bridgeProblem;
}
