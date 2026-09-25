using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Processes;
using Helm.Core.Settings;
using Helm.Modules.ClaudeChat.Cli;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.ClaudeChat;

/// <summary>
/// Owns the one running Claude Code process. The chat view model asks for a session when the user sends the first
/// message and reads its events; disabling the module (or Helm exiting / updating) ends it.
/// </summary>
public sealed class ClaudeChatModule : HelmModuleBase
{
    public const string ModuleId = "claude-chat";
    private const string HotkeyId = "show";

    private readonly IChildProcessLauncher _launcher;
    private readonly IHotkeyManager _hotkeys;
    private readonly ILogger<ClaudeChatModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeSession? _session;
    private HotkeyRegistration? _registration;

    public ClaudeChatModule(ISettingsStoreFactory settings, IChildProcessLauncher launcher, IHotkeyManager hotkeys, ILogger<ClaudeChatModule> logger)
    {
        Settings = settings.Get<ClaudeChatSettings>(ModuleId);
        _launcher = launcher;
        _hotkeys = hotkeys;
        _logger = logger;
        Settings.Changed += (_, _) =>
        {
            RefreshCli();
            _ = ApplyHotkeyAsync();
        };
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Claude Chat";
    public override string Description => "Chat with Claude Code right inside Helm. It uses the Claude Code you already have installed and signed in, works in the folder you pick, and asks before it changes anything.";
    public override ModuleGroup Group => ModuleGroup.Advanced;
    public override SymbolRegular Icon => SymbolRegular.ChatSparkle24;
    public override Type SettingsPageType => typeof(ClaudeChatPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys =>
        [new HotkeyDefinition(ModuleId, HotkeyId, "Show Claude Chat", Settings.Current.Hotkey)];

    public ISettingsStore<ClaudeChatSettings> Settings { get; }

    /// <summary>The CLI Helm will start, or null when Claude Code was not found.</summary>
    public ClaudeCli? Cli { get; private set; }

    /// <summary>Raised (on any thread) when <see cref="Cli"/> changes.</summary>
    public event EventHandler? CliChanged;

    /// <summary>Raised on the hotkey thread when the user asks to see the chat.</summary>
    public event EventHandler? RevealRequested;

    public override async Task EnableAsync(CancellationToken ct)
    {
        RefreshCli();
        await ApplyHotkeyAsync().ConfigureAwait(false);
    }

    public override async Task DisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _registration?.Dispose();
            _registration = null;
        }
        finally
        {
            _gate.Release();
        }
        await EndSessionAsync().ConfigureAwait(false);
    }

    /// <summary>Ends any current session and starts a new one with the current settings.</summary>
    /// <exception cref="ClaudeSessionException">Claude Code is missing or did not start.</exception>
    public async Task<ClaudeSession> StartSessionAsync(string? resumeSessionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EndSessionCoreAsync().ConfigureAwait(false);
            if (!IsEnabled) throw new ClaudeSessionException("Claude Chat is turned off.");

            RefreshCli();
            var cli = Cli ?? throw new ClaudeSessionException(MissingCliMessage(Settings.Current));
            var settings = Settings.Current;
            var folder = settings.ResolveWorkingDirectory();
            if (!Directory.Exists(folder)) throw new ClaudeSessionException($"The working folder '{folder}' does not exist.");

            var options = new ClaudeSessionOptions(folder)
            {
                Model = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim(),
                PermissionMode = settings.PermissionMode,
                ResumeSessionId = resumeSessionId,
            };
            _session = await ClaudeSession.StartAsync(_launcher, cli, options, _logger, ct).ConfigureAwait(false);
            return _session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EndSessionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndSessionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndSessionCoreAsync()
    {
        if (_session is not { } session) return;
        _session = null;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ending the Claude Code session failed");
        }
    }

    /// <summary>(Re)registers the hotkey while enabled; the gesture may have changed in the settings.</summary>
    private async Task ApplyHotkeyAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var definition = Hotkeys[0];
            if (!IsEnabled)
            {
                NotifyHotkeysChanged();
                return;
            }
            if (_registration is { IsRegistered: true } current && current.Definition.Gesture == definition.Gesture) return;
            _registration?.Dispose();
            _registration = await _hotkeys.TryRegisterAsync(definition, () => RevealRequested?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
            HotkeyError = _registration.IsRegistered ? null : $"The shortcut {definition.Gesture} is not active: {_registration.Error}";
            NotifyHotkeysChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registering the Claude Chat shortcut failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Why the shortcut is not active, or null. Shown on the page next to the other status.</summary>
    public string? HotkeyError
    {
        get => _hotkeyError;
        private set => SetProperty(ref _hotkeyError, value);
    }

    private string? _hotkeyError;

    private void RefreshCli()
    {
        var settings = Settings.Current;
        var cli = ClaudeCliLocator.Find(string.IsNullOrWhiteSpace(settings.CliPath) ? null : settings.CliPath.Trim());
        StatusMessage = cli is null && IsEnabled ? MissingCliMessage(settings) : null;
        if (cli == Cli) return;
        Cli = cli;
        CliChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string MissingCliMessage(ClaudeChatSettings settings) => string.IsNullOrWhiteSpace(settings.CliPath)
        ? "Claude Code was not found. Install it (https://claude.com/claude-code), sign in once in a terminal, then turn Claude Chat off and on."
        : $"Claude Code was not found at '{settings.CliPath}'.";
}
