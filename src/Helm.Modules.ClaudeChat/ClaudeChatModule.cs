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

    private readonly IChildProcessLauncher _launcher;
    private readonly ILogger<ClaudeChatModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeSession? _session;

    public ClaudeChatModule(ISettingsStoreFactory settings, IChildProcessLauncher launcher, ILogger<ClaudeChatModule> logger)
    {
        Settings = settings.Get<ClaudeChatSettings>(ModuleId);
        _launcher = launcher;
        _logger = logger;
        Settings.Changed += (_, _) => RefreshCli();
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Claude Chat";
    public override string Description => "Chat with Claude Code right inside Helm. It uses the Claude Code you already have installed and signed in, works in the folder you pick, and asks before it changes anything.";
    public override ModuleGroup Group => ModuleGroup.Advanced;
    public override SymbolRegular Icon => SymbolRegular.ChatSparkle24;
    public override Type SettingsPageType => typeof(ClaudeChatPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public ISettingsStore<ClaudeChatSettings> Settings { get; }

    /// <summary>The CLI Helm will start, or null when Claude Code was not found.</summary>
    public ClaudeCli? Cli { get; private set; }

    /// <summary>Raised (on any thread) when <see cref="Cli"/> changes.</summary>
    public event EventHandler? CliChanged;

    public override Task EnableAsync(CancellationToken ct)
    {
        RefreshCli();
        return Task.CompletedTask;
    }

    public override async Task DisableAsync() => await EndSessionAsync().ConfigureAwait(false);

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
