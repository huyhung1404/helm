using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.ClaudeChat.Chat;
using Helm.Modules.ClaudeChat.Cli;
using Microsoft.Extensions.Logging;

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

    public ClaudeChatViewModel(ClaudeChatModule module, IUiDispatcher ui, ILogger<ClaudeChatViewModel> logger)
    {
        Module = module;
        _store = module.Settings;
        _ui = ui;
        Chat = new ChatViewModel(module, ui, logger);
        _permissionMode = PermissionModes[0];
        Load(_store.Current);
        module.CliChanged += (_, _) => _ui.Post(() => OnPropertyChanged(nameof(CliDescription)));
    }

    public ClaudeChatModule Module { get; }

    /// <summary>The conversation; lives as long as Helm, wherever its view is shown.</summary>
    public ChatViewModel Chat { get; }

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

    partial void OnWorkingDirectoryChanged(string value) => Save(s => s.WorkingDirectory = value.Trim());
    partial void OnModelChanged(string value) => Save(s => s.Model = value.Trim());
    partial void OnPermissionModeChanged(PermissionModeOption value) => Save(s => s.PermissionMode = value.Mode);
    partial void OnCliPathChanged(string value) => Save(s => s.CliPath = value.Trim());

    private void Load(ClaudeChatSettings s)
    {
        _loading = true;
        WorkingDirectory = s.WorkingDirectory;
        Model = s.Model;
        PermissionMode = PermissionModes.FirstOrDefault(o => o.Mode == s.PermissionMode) ?? PermissionModes[0];
        CliPath = s.CliPath;
        _loading = false;
    }

    private void Save(Action<ClaudeChatSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }
}
