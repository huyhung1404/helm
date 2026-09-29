using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Processes;
using Helm.Core.Settings;
using Helm.Modules.ClaudeChat.Chat;
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
    private readonly HashSet<ClaudeSession> _sessions = [];
    private int _probing;
    private HotkeyRegistration? _registration;

    private readonly Helm.Core.Mcp.McpClientConfig? _mcp;

    public ClaudeChatModule(ISettingsStoreFactory settings, IChildProcessLauncher launcher, IHotkeyManager hotkeys, ILogger<ClaudeChatModule> logger,
        Helm.Core.Mcp.McpClientConfig? mcp = null)
    {
        _mcp = mcp;
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
    public override ImageSource IconImage => ClaudeLogo.Image;
    public override Type SettingsPageType => typeof(ClaudeChatPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys =>
        [new HotkeyDefinition(ModuleId, HotkeyId, "Show Claude Chat", Settings.Current.Hotkey)];

    public ISettingsStore<ClaudeChatSettings> Settings { get; }

    /// <summary>The CLI Helm will start, or null when Claude Code was not found.</summary>
    public ClaudeCli? Cli { get; private set; }

    /// <summary>Raised (on any thread) when <see cref="Cli"/> changes.</summary>
    public event EventHandler? CliChanged;

    /// <summary>Commands and models from the latest initialize handshake (empty until one ran).</summary>
    public ClaudeCapabilities Capabilities { get; private set; } = ClaudeCapabilities.Empty;

    /// <summary>Raised (on any thread) when <see cref="Capabilities"/> changes.</summary>
    public event EventHandler? CapabilitiesChanged;

    /// <summary>
    /// Fills <see cref="Capabilities"/> before any chat has started, with a short-lived Claude Code process in
    /// <paramref name="folder"/> (the handshake uses no model, so it costs nothing). Does nothing when known.
    /// </summary>
    public async Task EnsureCapabilitiesAsync(string folder)
    {
        if (Capabilities.Commands.Count > 0 || !IsEnabled || Interlocked.Exchange(ref _probing, 1) == 1) return;
        try
        {
            var session = await StartSessionAsync(folder, null, null, CancellationToken.None).ConfigureAwait(false);
            await EndSessionAsync(session).ConfigureAwait(false);
        }
        catch (ClaudeSessionException ex)
        {
            _logger.LogDebug(ex, "Could not read Claude Code's commands");
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }

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
        ClaudeSession[] running;
        lock (_sessions)
        {
            running = [.. _sessions];
            _sessions.Clear();
        }
        await Task.WhenAll(running.Select(EndCoreAsync)).ConfigureAwait(false);
    }

    /// <summary>Starts one more Claude Code process (every open chat has its own).</summary>
    /// <param name="folder">The chat's working folder.</param>
    /// <param name="model">Overrides the model from the settings (null = the settings' choice).</param>
    /// <exception cref="ClaudeSessionException">Claude Code is missing or did not start.</exception>
    public async Task<ClaudeSession> StartSessionAsync(string folder, string? resumeSessionId, string? model, CancellationToken ct)
    {
        if (!IsEnabled) throw new ClaudeSessionException("Claude Chat is turned off.");
        RefreshCli();
        var settings = Settings.Current;
        var cli = Cli ?? throw new ClaudeSessionException(MissingCliMessage(settings));
        if (!Directory.Exists(folder)) throw new ClaudeSessionException($"The working folder '{folder}' does not exist.");

        var options = new ClaudeSessionOptions(folder)
        {
            Model = model ?? (string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim()),
            PermissionMode = settings.PermissionMode,
            ResumeSessionId = resumeSessionId,
            McpConfigPath = TryWriteMcpConfig(),
        };
        var session = await ClaudeSession.StartAsync(_launcher, cli, options, _logger, ct).ConfigureAwait(false);
        lock (_sessions) _sessions.Add(session);
        var capabilities = ClaudeProtocol.ParseCapabilities(session.Capabilities);
        if (capabilities.Commands.Count > 0 || capabilities.Models.Count > 0)
        {
            Capabilities = capabilities;
            CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        }
        if (!IsEnabled) await EndSessionAsync(session).ConfigureAwait(false); // turned off while it was starting
        return session;
    }

    /// <summary>Ends one chat's Claude Code process (closing its tab, a new chat in that tab).</summary>
    public Task EndSessionAsync(ClaudeSession session)
    {
        lock (_sessions)
        {
            if (!_sessions.Remove(session)) return Task.CompletedTask;
        }
        return EndCoreAsync(session);
    }

    private async Task EndCoreAsync(ClaudeSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ending a Claude Code session failed");
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

    /// <summary>Helm's own tools for the chat, when they are on; a chat without them is better than no chat.</summary>
    private string? TryWriteMcpConfig()
    {
        try
        {
            return _mcp?.WriteConfigFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the MCP config for Claude Chat");
            return null;
        }
    }

    private static string MissingCliMessage(ClaudeChatSettings settings) => string.IsNullOrWhiteSpace(settings.CliPath)
        ? "Claude Code was not found. Install it (https://claude.com/claude-code), sign in once in a terminal, then turn Claude Chat off and on."
        : $"Claude Code was not found at '{settings.CliPath}'.";
}
