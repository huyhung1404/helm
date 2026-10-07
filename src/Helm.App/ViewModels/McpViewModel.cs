using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.App.Mcp;
using Helm.Core.Mcp;
using Helm.Core.Modules;
using Helm.Core.Processes;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.App.ViewModels;

/// <summary>The tools of one risk that AI agents are offered now (or that the switches hold back).</summary>
internal sealed record McpToolGroup(string Title, string Description, IReadOnlyList<McpTool> Tools, string? Empty)
{
    public bool HasTools => Tools.Count > 0;
}

/// <summary>One client connected now, as the Connections card shows it.</summary>
internal sealed record McpConnectionRow(Guid Id, string Name, string Detail);

/// <summary>One client the Connect card explains: its status, and what to copy or run.</summary>
internal sealed partial class McpClientRow(McpClientKind kind, string title, string where) : ObservableObject
{
    public McpClientKind Kind { get; } = kind;
    public string Title { get; } = title;

    /// <summary>The config file this client reads (shown; Helm only ever reads Helm's own entry in it).</summary>
    public string Where { get; } = where;

    [ObservableProperty] private McpClientState _state;
    [ObservableProperty] private string _statusText = "";

    public bool IsAdded => State == McpClientState.Added;
    public bool IsElsewhere => State == McpClientState.AddedElsewhere;

    partial void OnStateChanged(McpClientState value)
    {
        OnPropertyChanged(nameof(IsAdded));
        OnPropertyChanged(nameof(IsElsewhere));
    }
}

/// <summary>The AI &amp; MCP page: how AI agents (Claude Code, VS Code, any MCP client) reach Helm's tools, and what they may do.</summary>
internal sealed partial class McpViewModel : ObservableObject
{
    private readonly ISettingsStore<McpSettings> _store;
    private readonly McpClientConfig _config;
    private readonly McpPipeHost _host;
    private readonly IEnumerable<IMcpToolProvider> _providers;
    private readonly IModuleHost _modules;
    private readonly IUiDispatcher _ui;
    private readonly IProcessLauncher _launcher;
    private readonly McpClientSetup _setup;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _allowChanges;
    [ObservableProperty] private IReadOnlyList<McpToolGroup> _toolGroups = [];
    [ObservableProperty] private string _toolsSummary = "";
    [ObservableProperty] private bool _isClaudeBusy;
    [ObservableProperty] private string _claudeOutput = "";
    [ObservableProperty] private bool _claudeFailed;
    [ObservableProperty] private bool _claudeMissing;

    public McpViewModel(McpPermissionsViewModel permissions, ISettingsStoreFactory settings, McpClientConfig config, McpPipeHost host,
        IEnumerable<IMcpToolProvider> providers, IModuleHost modules, IUiDispatcher ui, IProcessLauncher launcher,
        IChildProcessLauncher childLauncher, ILoggerFactory loggers)
    {
        Permissions = permissions;
        _store = settings.Get<McpSettings>(McpSettings.StoreId);
        _config = config;
        _host = host;
        _providers = providers;
        _modules = modules;
        _ui = ui;
        _launcher = launcher;
        _setup = new McpClientSetup(config, childLauncher, loggers.CreateLogger<McpClientSetup>());

        _enabled = _store.Current.Enabled;
        _allowChanges = _store.Current.AllowChanges;
        var folders = McpClientFolders.Current;
        ClaudeCode = new McpClientRow(McpClientKind.ClaudeCode, "Claude Code", McpClientConfig.ConfigFile(McpClientKind.ClaudeCode, folders));
        VsCode = new McpClientRow(McpClientKind.VsCode, "VS Code", McpClientConfig.ConfigFile(McpClientKind.VsCode, folders));
        ClaudeDesktop = new McpClientRow(McpClientKind.ClaudeDesktop, "Claude Desktop", McpClientConfig.ConfigFile(McpClientKind.ClaudeDesktop, folders));

        _store.Changed += (_, current) => _ui.Post(() =>
        {
            Enabled = current.Enabled;
            AllowChanges = current.AllowChanges;
        });
        _host.ConnectionsChanged += (_, _) => _ui.Post(RefreshConnections);
        RefreshConnections();
        RefreshTools();
    }

    public McpPermissionsViewModel Permissions { get; }

    public McpClientRow ClaudeCode { get; }
    public McpClientRow VsCode { get; }
    public McpClientRow ClaudeDesktop { get; }

    public string ClaudeCodeCommand => _config.ClaudeCodeCommand;
    public string VsCodeSnippet => _config.VsCodeSnippet;
    public string ClaudeDesktopSnippet => _config.ClaudeDesktopSnippet;
    public string StartCommand => _config.StartCommand;
    public string McpServersSnippet => _config.McpServersSnippet;

    /// <summary>Set when this Helm runs on a test data folder: every client then starts it with --data-dir.</summary>
    public bool IsTestCopy => _config.Arguments.Count > 1;

    public ObservableCollection<McpConnectionRow> Connections { get; } = [];

    public bool HasConnections => Connections.Count > 0;

    /// <summary>The page opened: read the clients' files and the modules again (they may have changed meanwhile).</summary>
    public void Refresh()
    {
        RefreshTools();
        RefreshClients();
        RefreshConnections();
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_store.Current.Enabled != value) _store.Update(s => s.Enabled = value);
        if (!value) DisconnectAll();
        RefreshTools();
    }

    partial void OnAllowChangesChanged(bool value)
    {
        if (_store.Current.AllowChanges != value) _store.Update(s => s.AllowChanges = value);
        if (!value) DisconnectAll();
        RefreshTools();
    }

    /// <summary>A connection keeps the tools it got when it connected: switching off has to end it.</summary>
    private void DisconnectAll()
    {
        foreach (var connection in _host.Connections) _host.Disconnect(connection.Id);
    }

    [RelayCommand]
    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy; the text stays selectable in its box.
        }
    }

    [RelayCommand]
    private Task AddToClaudeCode() => RunClaudeAsync(ct => _setup.AddAsync(ct));

    [RelayCommand]
    private Task RemoveFromClaudeCode() => RunClaudeAsync(ct => _setup.RemoveAsync(ct));

    [RelayCommand]
    private void OpenFolder(McpClientRow? client)
    {
        if (client is null) return;
        var folder = Path.GetDirectoryName(client.Where);
        if (folder is null || !Directory.Exists(folder))
        {
            client.StatusText = $"{client.Title} has no settings folder yet ({folder}). Install and start it once first.";
            return;
        }
        _launcher.OpenFolder(folder);
    }

    [RelayCommand]
    private void OpenClaudeCodeSetup() => _launcher.OpenUrl("https://docs.claude.com/en/docs/claude-code/setup");

    [RelayCommand]
    private void Disconnect(McpConnectionRow? row)
    {
        if (row is not null) _host.Disconnect(row.Id);
    }

    private async Task RunClaudeAsync(Func<CancellationToken, Task<McpClientSetupResult>> run)
    {
        if (IsClaudeBusy) return;
        IsClaudeBusy = true;
        ClaudeMissing = _setup.FindClaude() is null;
        try
        {
            var result = await run(CancellationToken.None);
            ClaudeOutput = result.Output;
            ClaudeFailed = !result.Succeeded;
        }
        finally
        {
            IsClaudeBusy = false;
            RefreshClients();
        }
    }

    private void RefreshClients()
    {
        foreach (var client in (McpClientRow[])[ClaudeCode, VsCode, ClaudeDesktop])
        {
            var status = _config.Status(client.Kind);
            client.State = status.State;
            client.StatusText = status.State switch
            {
                McpClientState.Added => "Added: it starts this Helm.",
                McpClientState.AddedElsewhere => client.Kind == McpClientKind.ClaudeCode
                    ? "Added, but it starts another Helm.exe (Helm moved, or another copy). Add it again to point it here."
                    : "Added, but it starts another Helm.exe (Helm moved, or another copy). Replace Helm's entry with the one below.",
                McpClientState.Unreadable => $"Not sure: {Path.GetFileName(status.File)} could not be read as JSON. Helm leaves it alone.",
                _ => "Not added.",
            };
        }
    }

    private void RefreshConnections()
    {
        var now = DateTimeOffset.Now;
        Connections.Clear();
        foreach (var c in _host.Connections)
        {
            var name = string.IsNullOrWhiteSpace(c.ClientVersion) ? c.ClientName : $"{c.ClientName} {c.ClientVersion}";
            var since = c.Since.Date == now.Date ? c.Since.ToString("HH:mm") : c.Since.ToString("d MMM, HH:mm");
            var calls = c.Calls == 1 ? "1 call" : $"{c.Calls} calls";
            Connections.Add(new McpConnectionRow(c.Id, name, $"Connected since {since} · {calls}"));
        }
        OnPropertyChanged(nameof(HasConnections));
    }

    private void RefreshTools()
    {
        var all = _providers
            .Where(p => p.ModuleId is not { } id || _modules.Find(id)?.IsEnabled == true)
            .SelectMany(p => p.Tools)
            .GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        // The same rule as the server Helm builds for each connection (HelmHost).
        var offered = Enabled ? all.Where(t => AllowChanges || t.ReadOnly).ToList() : [];
        ToolGroups =
        [
            Group(McpRisk.Read, "Read", "Only read Helm's own data."),
            Group(McpRisk.Change, "Change", "Change Helm's data; changes sync to your other devices."),
            Group(McpRisk.Remote, "Remote", "Run something on another machine (a server's SSH menu). Always asks you first."),
        ];
        ToolsSummary = !Enabled
            ? "Off: AI agents get no Helm tools, even where Helm is added."
            : offered.Count == 1 ? "1 tool is offered now." : $"{offered.Count} tools are offered now.";

        McpToolGroup Group(McpRisk risk, string title, string description)
        {
            var tools = offered.Where(t => t.Risk == risk).ToList();
            string? empty = null;
            if (tools.Count == 0)
            {
                var hidden = all.Count(t => t.Risk == risk);
                empty = !Enabled ? "None while Helm's tools are off."
                    : hidden > 0 ? $"None now: turn on “AI agents may change things” to offer {hidden}."
                    : "None: no tool that is on does this.";
            }
            return new McpToolGroup($"{title} ({tools.Count})", description, tools, empty);
        }
    }
}
