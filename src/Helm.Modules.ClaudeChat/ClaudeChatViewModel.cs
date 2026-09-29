using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.ClaudeChat.Chat;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Modules.ClaudeChat;

public sealed record PermissionModeOption(ClaudePermissionMode Mode, string Title, string Description);

public sealed partial class ClaudeChatViewModel : ObservableObject
{
    private readonly ISettingsStore<ClaudeChatSettings> _store;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private PermissionModeOption _permissionMode;
    [ObservableProperty] private string _cliPath = string.Empty;
    [ObservableProperty] private HotkeyGesture _hotkey;

    private readonly ChatWindowHost _chatWindow;

    private readonly ISettingsStore<Helm.Core.Mcp.McpSettings>? _mcpStore;
    private readonly Helm.Core.Mcp.McpClientConfig? _mcp;

    [ObservableProperty] private bool _mcpEnabled;
    [ObservableProperty] private bool _mcpAllowChanges;

    public ClaudeChatViewModel(ClaudeChatModule module, ChatWindowHost chatWindow, IUiDispatcher ui, ISettingsStoreFactory? settings = null,
        Helm.Core.Mcp.McpClientConfig? mcp = null)
    {
        _mcpStore = settings?.Get<Helm.Core.Mcp.McpSettings>(Helm.Core.Mcp.McpSettings.StoreId);
        _mcp = mcp;
        _mcpEnabled = _mcpStore?.Current.Enabled ?? false;
        _mcpAllowChanges = _mcpStore?.Current.AllowChanges ?? false;
        Module = module;
        _chatWindow = chatWindow;
        _store = module.Settings;
        _ui = ui;
        _permissionMode = PermissionModes[0];
        Load(_store.Current);
        // The chat can change the working folder too (new chat in another folder); keep the page in sync.
        _store.Changed += (_, current) => _ui.Post(() => Load(current));
        module.CliChanged += (_, _) => _ui.Post(() => OnPropertyChanged(nameof(CliDescription)));
    }

    public ClaudeChatModule Module { get; }

    [RelayCommand]
    private void OpenChat() => _chatWindow.Show();

    public HotkeyGesture DefaultHotkey => ClaudeChatSettings.DefaultHotkey;

    public IReadOnlyList<string> Models { get; } = ["", "opus", "sonnet", "haiku"];

    public IReadOnlyList<PermissionModeOption> PermissionModes { get; } =
    [
        new(ClaudePermissionMode.Manual, "Ask before changes", "Anything not already allowed in your Claude Code settings needs your OK."),
        new(ClaudePermissionMode.AcceptEdits, "Allow file edits", "Edits inside the working folder run without asking; commands still ask."),
        new(ClaudePermissionMode.Plan, "Plan only", "Claude reads and plans but changes nothing."),
    ];

    public string DefaultWorkingDirectory => new ClaudeChatSettings().ResolveWorkingDirectory();

    public string CliDescription => Module.Cli is { } cli ? $"Using {cli.Path}" : "Claude Code was not found on PATH.";

    [RelayCommand]
    private void BrowseWorkingDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder Claude Code works in",
            InitialDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : DefaultWorkingDirectory,
        };
        if (dialog.ShowDialog() == true) WorkingDirectory = dialog.FolderName;
    }

    /// <summary>The line that adds Helm's tools to Claude Code in a terminal.</summary>
    public string McpCommand => _mcp?.ClaudeCodeCommand ?? "";

    partial void OnMcpEnabledChanged(bool value) => _mcpStore?.Update(s => s.Enabled = value);

    partial void OnMcpAllowChangesChanged(bool value) => _mcpStore?.Update(s => s.AllowChanges = value);

    [RelayCommand]
    private void CopyMcpCommand()
    {
        try
        {
            System.Windows.Clipboard.SetText(McpCommand);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy; the command stays selectable in its box.
        }
    }

    partial void OnWorkingDirectoryChanged(string value) => Save(s => s.WorkingDirectory = value.Trim());
    partial void OnModelChanged(string value) => Save(s => s.Model = value.Trim());
    partial void OnPermissionModeChanged(PermissionModeOption value) => Save(s => s.PermissionMode = value.Mode);
    partial void OnCliPathChanged(string value) => Save(s => s.CliPath = value.Trim());
    partial void OnHotkeyChanged(HotkeyGesture value) => Save(s => s.Hotkey = value);

    private void Load(ClaudeChatSettings s)
    {
        _loading = true;
        WorkingDirectory = s.WorkingDirectory;
        Model = s.Model;
        PermissionMode = PermissionModes.FirstOrDefault(o => o.Mode == s.PermissionMode) ?? PermissionModes[0];
        CliPath = s.CliPath;
        Hotkey = s.Hotkey;
        _loading = false;
    }

    private void Save(Action<ClaudeChatSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }
}
