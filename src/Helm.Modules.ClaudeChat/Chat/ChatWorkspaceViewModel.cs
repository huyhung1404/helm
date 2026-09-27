using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Modules.ClaudeChat.Cli;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>A project folder in the tree, with the chats open in it.</summary>
public sealed partial class ChatFolder(string path) : ObservableObject
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;

    public ObservableCollection<ChatViewModel> Chats { get; } = [];

    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Tree selection of the folder row itself (only chats are "selected" for the right-hand side).</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The chat window's model: several project folders, each with several chats running side by side (one Claude
/// Code process per chat). It tells the user when a chat they are not looking at finishes, fails or needs them,
/// and remembers the open folders and chats for next time. UI thread only.
/// </summary>
public sealed partial class ChatWorkspaceViewModel : ObservableObject
{
    private readonly ClaudeChatModule _module;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifications _notifications;
    private readonly ILogger<ChatViewModel> _chatLogger;
    private readonly Dictionary<ChatViewModel, PastSession> _notLoaded = [];
    private bool _restored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFolder), nameof(HasChat))]
    private ChatViewModel? _selectedChat;

    /// <summary>The chats saved in the selected chat's folder, filtered by <see cref="HistoryFilter"/> (the clock button).</summary>
    [ObservableProperty] private IReadOnlyList<PastSession> _history = [];

    [ObservableProperty] private string _historyFilter = string.Empty;

    [ObservableProperty] private bool _isHistoryOpen;

    public ChatWorkspaceViewModel(ClaudeChatModule module, IUiDispatcher ui, IUserNotifications notifications, ILogger<ChatViewModel> chatLogger)
    {
        _module = module;
        _ui = ui;
        _notifications = notifications;
        _chatLogger = chatLogger;
        module.CapabilitiesChanged += (_, _) => _ui.Post(() =>
        {
            OnPropertyChanged(nameof(Capabilities));
            OnPropertyChanged(nameof(ModelChoices));
        });
    }

    public ObservableCollection<ChatFolder> Folders { get; } = [];

    public ChatFolder? SelectedFolder => SelectedChat is { } chat ? FolderOf(chat) : Folders.FirstOrDefault();

    public bool HasChat => SelectedChat is not null;

    /// <summary>Claude Code's commands and models, for the composer's "/" menu and model picker.</summary>
    public ClaudeCapabilities Capabilities => _module.Capabilities;

    /// <summary>The composer's model picker: the CLI's list once known ("" = the default from the settings).</summary>
    public IReadOnlyList<ModelChoice> ModelChoices => _module.Capabilities.Models.Count > 0
        ? [new ModelChoice(string.Empty, "Default", "The model from Claude Chat settings"), .. _module.Capabilities.Models.Where(m => m.Value != "default")]
        : [new ModelChoice(string.Empty, "Default", null), new("opus", "Opus", null), new("sonnet", "Sonnet", null), new("haiku", "Haiku", null)];

    public IEnumerable<PastSession> FilteredHistory => string.IsNullOrWhiteSpace(HistoryFilter)
        ? History
        : History.Where(h => h.Title.Contains(HistoryFilter.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Set by the window host: whether the chat window has focus (no notification for what the user is looking at).</summary>
    public Func<bool> IsWindowActive { get; set; } = () => false;

    /// <summary>A notification was clicked: bring the window up on <see cref="SelectedChat"/>.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Opens the folders and chats of last time, or the default folder with one new chat.</summary>
    public void EnsureRestored()
    {
        if (_restored) return;
        _restored = true;
        var settings = _module.Settings.Current;
        foreach (var path in settings.OpenFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = AddFolderNode(path);
            foreach (var open in settings.OpenChats.Where(c => string.Equals(c.Folder, path, StringComparison.OrdinalIgnoreCase)))
            {
                if (ClaudeSessionStore.Find(path, open.SessionId) is not { } past) continue;
                var chat = CreateChat(folder);
                chat.Title = past.Title;
                chat.SessionId = past.SessionId;
                _notLoaded[chat] = past; // read its transcript when it is first shown
            }
        }
        if (Folders.Count == 0) AddFolderNode(settings.ResolveWorkingDirectory());
        foreach (var folder in Folders.Where(f => f.Chats.Count == 0)) CreateChat(folder);
        SelectedChat = Folders.SelectMany(f => f.Chats).FirstOrDefault();
        _ = _module.EnsureCapabilitiesAsync(Folders[0].Path);
    }

    // ---- folders and chats --------------------------------------------------------------------------------------

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add a folder to Claude Chat" };
        if (SelectedFolder is { } current && Directory.Exists(current.Path)) dialog.InitialDirectory = current.Path;
        if (dialog.ShowDialog(ActiveWindow()) != true) return;
        var folder = Folders.FirstOrDefault(f => SamePath(f.Path, dialog.FolderName)) ?? AddFolderNode(dialog.FolderName);
        folder.IsExpanded = true;
        SelectedChat = folder.Chats.FirstOrDefault(c => c.IsEmpty) ?? CreateChat(folder);
        _module.Settings.Update(s => s.WorkingDirectory = folder.Path); // the default for next time
        Save();
    }

    /// <summary>A new chat in <paramref name="folder"/> (or the selected chat's folder), reusing an empty one.</summary>
    [RelayCommand]
    private void NewChat(ChatFolder? folder)
    {
        folder ??= SelectedFolder;
        if (folder is null) return;
        folder.IsExpanded = true;
        SelectedChat = folder.Chats.FirstOrDefault(c => c.IsEmpty && c.SessionId is null && !_notLoaded.ContainsKey(c)) ?? CreateChat(folder);
    }

    [RelayCommand]
    private async Task CloseChatAsync(ChatViewModel? chat)
    {
        if (chat is null || FolderOf(chat) is not { } folder) return;
        var index = folder.Chats.IndexOf(chat);
        await chat.CloseAsync();
        folder.Chats.Remove(chat);
        _notLoaded.Remove(chat);
        if (ReferenceEquals(SelectedChat, chat))
        {
            SelectedChat = folder.Chats.ElementAtOrDefault(Math.Min(index, folder.Chats.Count - 1))
                ?? Folders.SelectMany(f => f.Chats).FirstOrDefault();
        }
        Save();
    }

    /// <summary>"/ Clear conversation": the chat starts over in the same place (the old one stays in history).</summary>
    public async Task ClearChatAsync(ChatViewModel chat)
    {
        if (FolderOf(chat) is not { } folder) return;
        var index = folder.Chats.IndexOf(chat);
        await chat.CloseAsync();
        _notLoaded.Remove(chat);
        var fresh = CreateChat(folder);
        folder.Chats.Move(folder.Chats.IndexOf(fresh), index);
        folder.Chats.Remove(chat);
        fresh.SelectedModel = chat.SelectedModel;
        SelectedChat = fresh;
        Save();
    }

    /// <summary>Closes the folder's chats (they stay in its history) and takes it off the tree.</summary>
    [RelayCommand]
    private async Task RemoveFolderAsync(ChatFolder? folder)
    {
        if (folder is null) return;
        foreach (var chat in folder.Chats.ToList())
        {
            await chat.CloseAsync();
            _notLoaded.Remove(chat);
        }
        Folders.Remove(folder);
        if (SelectedChat is { } selected && FolderOf(selected) is null) SelectedChat = Folders.SelectMany(f => f.Chats).FirstOrDefault();
        Save();
    }

    partial void OnSelectedChatChanged(ChatViewModel? oldValue, ChatViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is null) return;
        newValue.IsSelected = true;
        if (_notLoaded.Remove(newValue, out var past)) _ = newValue.LoadPastAsync(past);
        IsHistoryOpen = false;
    }

    // ---- history (the clock button) -----------------------------------------------------------------------------

    partial void OnIsHistoryOpenChanged(bool value)
    {
        if (value) _ = RefreshHistoryAsync();
    }

    partial void OnHistoryFilterChanged(string value) => OnPropertyChanged(nameof(FilteredHistory));

    partial void OnHistoryChanged(IReadOnlyList<PastSession> value) => OnPropertyChanged(nameof(FilteredHistory));

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        HistoryFilter = string.Empty;
        var folder = SelectedFolder?.Path;
        History = folder is null ? [] : await Task.Run(() => ClaudeSessionStore.List(folder, max: 40));
    }

    /// <summary>A saved chat: its node when it is already open, else the selected empty chat, else a new chat.</summary>
    [RelayCommand]
    private async Task OpenPastAsync(PastSession? past)
    {
        if (past is null) return;
        IsHistoryOpen = false;
        var open = Folders.SelectMany(f => f.Chats).FirstOrDefault(c => c.SessionId == past.SessionId);
        if (open is not null)
        {
            SelectedChat = open;
            return;
        }
        var folderPath = past.WorkingDirectory is { Length: > 0 } w && Directory.Exists(w) ? w : SelectedFolder?.Path;
        if (folderPath is null) return;
        var folder = Folders.FirstOrDefault(f => SamePath(f.Path, folderPath)) ?? AddFolderNode(folderPath);
        var target = SelectedChat is { IsEmpty: true, SessionId: null, State: ChatState.Idle } empty && ReferenceEquals(FolderOf(empty), folder) && !_notLoaded.ContainsKey(empty)
            ? empty
            : CreateChat(folder);
        SelectedChat = target;
        await target.LoadPastAsync(past);
        Save();
    }

    // ---- notifications ------------------------------------------------------------------------------------------

    private void OnAttention(ChatViewModel chat, ChatAttention kind)
    {
        if (chat.IsSelected && IsWindowActive()) return; // the user is looking at it
        var where = FolderOf(chat)?.Name;
        var (title, message) = kind switch
        {
            ChatAttention.NeedsYou => ("Claude needs your OK", $"{chat.Title} · {where}: a tool is waiting for permission."),
            ChatAttention.Failed => ("Claude stopped with an error", $"{chat.Title} · {where}"),
            _ => ("Claude finished", $"{chat.Title} · {where}"),
        };
        _notifications.Show(title, message, () =>
        {
            SelectedChat = chat;
            ShowRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private ChatFolder AddFolderNode(string path)
    {
        var folder = new ChatFolder(Path.GetFullPath(path));
        Folders.Add(folder);
        return folder;
    }

    private ChatViewModel CreateChat(ChatFolder folder)
    {
        var chat = new ChatViewModel(_module, _ui, _chatLogger, folder.Path);
        chat.AttentionRaised += (_, kind) => OnAttention(chat, kind);
        chat.TurnFinished += (_, _) => OnTurnFinished(chat);
        folder.Chats.Add(chat);
        return chat;
    }

    /// <summary>A new session id is worth remembering, and the CLI may have named the chat by now.</summary>
    private void OnTurnFinished(ChatViewModel chat)
    {
        Save();
        if (chat.SessionId is not { } id || FolderOf(chat) is not { } folder) return;
        _ = Task.Run(() => ClaudeSessionStore.Find(folder.Path, id)).ContinueWith(t =>
        {
            if (t.Result is { Title: { Length: > 0 } title }) _ui.Post(() => chat.Title = title);
        }, TaskScheduler.Default);
    }

    private ChatFolder? FolderOf(ChatViewModel chat) => Folders.FirstOrDefault(f => f.Chats.Contains(chat));

    private void Save()
    {
        var folders = Folders.Select(f => f.Path).ToList();
        var chats = Folders.SelectMany(f => f.Chats.Where(c => c.SessionId is not null).Select(c => new ClaudeChatSettings.OpenChat(f.Path, c.SessionId!))).ToList();
        _module.Settings.Update(s =>
        {
            s.OpenFolders = folders;
            s.OpenChats = chats;
        });
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static System.Windows.Window? ActiveWindow() =>
        System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current?.MainWindow;
}
