using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Modules.ClaudeChat.Cli;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.ClaudeChat.Chat;

public enum ChatState
{
    /// <summary>No turn running; a message can be sent (a session starts on demand).</summary>
    Idle,

    /// <summary>Claude Code is starting.</summary>
    Starting,

    /// <summary>A turn is running.</summary>
    Busy,

    /// <summary>A turn is paused on a permission card the user has not answered yet.</summary>
    WaitingForPermission,

    /// <summary>An interrupt was sent; waiting for the turn to end.</summary>
    Stopping,
}

/// <summary>What the chat's row in the tree shows, and what a notification is about.</summary>
public enum ChatAttention
{
    None,

    /// <summary>A reply is running.</summary>
    Working,

    /// <summary>A permission card is waiting for the user.</summary>
    NeedsYou,

    /// <summary>A reply finished while the user was looking elsewhere.</summary>
    Done,

    /// <summary>The reply or the process failed while the user was looking elsewhere.</summary>
    Failed,
}

/// <summary>
/// One chat of the tree: its rows, draft, folder and its own Claude Code process (started with the first message).
/// All members are UI-thread only; session events are marshalled through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    public const string NewChatTitle = "New chat";
    private const int TitleLength = 48;

    private readonly ClaudeChatModule _module;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly Dictionary<string, ToolChatItem> _tools = new(StringComparer.Ordinal);
    private readonly List<PermissionChatItem> _pendingPermissions = [];
    private ClaudeSession? _session;
    private AssistantChatItem? _streaming;
    private bool _endingOnPurpose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanSend), nameof(StatusText), nameof(CanChangeFolder), nameof(Attention))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private ChatState _state;

    /// <summary>Set by the workspace: the chat shown on the right. Seeing a chat clears its Done/Failed mark.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Attention))]
    private bool _isSelected;

    /// <summary>A finished (or failed) reply the user has not looked at yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Attention))]
    private ChatAttention _unseen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    /// <summary>The model the CLI reported for the running session (full id).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _model;

    /// <summary>The folder this chat works in. Changeable until the first message.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderName))]
    private string _folder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CostText))]
    private double _sessionCostUsd;

    /// <summary>The model picked in the composer; empty = the default from the settings.</summary>
    [ObservableProperty]
    private string _selectedModel = string.Empty;

    /// <summary>The CLI's id for this conversation (for resuming it later).</summary>
    [ObservableProperty]
    private string? _sessionId;

    /// <summary>Tab caption: the CLI's generated title once known, else the first message.</summary>
    [ObservableProperty]
    private string _title = NewChatTitle;

    public ChatViewModel(ClaudeChatModule module, IUiDispatcher ui, ILogger logger, string folder)
    {
        _module = module;
        _ui = ui;
        _logger = logger;
        _folder = folder;
    }

    /// <summary>Raised when a turn ends (so the tree can pick up the CLI's title for the chat).</summary>
    public event EventHandler? TurnFinished;

    /// <summary>Raised when the chat needs the user: a reply finished or failed, or a permission card appeared.</summary>
    public event EventHandler<ChatAttention>? AttentionRaised;

    public ChatAttention Attention => State == ChatState.WaitingForPermission ? ChatAttention.NeedsYou
        : IsBusy ? ChatAttention.Working
        : Unseen;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) Unseen = ChatAttention.None;
    }

    private void RaiseAttention(ChatAttention kind)
    {
        if (!IsSelected && kind is ChatAttention.Done or ChatAttention.Failed) Unseen = kind;
        AttentionRaised?.Invoke(this, kind);
    }

    public ObservableCollection<ChatItem> Items { get; } = [];

    /// <summary>Choices for the composer's model picker ("" = the default from the settings).</summary>
    public IReadOnlyList<string> Models { get; } = ["", "opus", "sonnet", "haiku"];

    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name : Folder;

    public bool IsBusy => State is ChatState.Starting or ChatState.Busy or ChatState.WaitingForPermission or ChatState.Stopping;

    public bool CanSend => State == ChatState.Idle && !string.IsNullOrWhiteSpace(Draft);

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The folder belongs to the conversation once it has started.</summary>
    public bool CanChangeFolder => IsEmpty && SessionId is null && State == ChatState.Idle;

    public string? StatusText => State switch
    {
        ChatState.Starting => "Starting Claude Code…",
        ChatState.Busy => "Claude is working…",
        ChatState.WaitingForPermission => "Waiting for your answer…",
        ChatState.Stopping => "Stopping…",
        _ => Model,
    };

    /// <summary>What the turns would cost at API prices; with a Claude subscription nothing is charged per token.</summary>
    public string? CostText => SessionCostUsd > 0 ? string.Create(CultureInfo.InvariantCulture, $"≈ ${SessionCostUsd:0.000}") : null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0 || State != ChatState.Idle) return;
        Draft = string.Empty;
        Add(new UserChatItem(text));
        if (Title == NewChatTitle) Title = Shorten(text);

        try
        {
            if (_session is null || _session.HasExited)
            {
                State = ChatState.Starting;
                Attach(await _module.StartSessionAsync(Folder, SessionId, NullIfEmpty(SelectedModel), CancellationToken.None));
            }
            State = ChatState.Busy;
            await _session!.SendUserMessageAsync(text);
        }
        catch (ClaudeSessionException ex)
        {
            _logger.LogWarning(ex, "Could not send to Claude Code");
            Add(new NoticeChatItem(ex.Message, NoticeKind.Error));
            State = ChatState.Idle;
        }
    }

    private bool CanStop => State is ChatState.Busy or ChatState.WaitingForPermission;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_session is not { } session) return;
        State = ChatState.Stopping;
        try
        {
            await session.InterruptAsync();
        }
        catch (ClaudeSessionException ex)
        {
            _logger.LogWarning(ex, "Interrupt failed");
        }
    }

    /// <summary>Switches the running session right away; otherwise the pick applies when the session starts.</summary>
    partial void OnSelectedModelChanged(string value) => _ = ApplyModelAsync(value);

    private async Task ApplyModelAsync(string value)
    {
        if (_session is not { HasExited: false } session || NullIfEmpty(value) is not { } model) return;
        try
        {
            await session.SetModelAsync(model); // the CLI confirms with its own "Set model to …" notice
        }
        catch (ClaudeSessionException ex)
        {
            Add(new NoticeChatItem(ex.Message, NoticeKind.Error));
        }
    }

    /// <summary>Fills this (empty) tab with a saved conversation; the next message continues it (<c>--resume</c>).</summary>
    public async Task LoadPastAsync(PastSession past)
    {
        var transcript = await Task.Run(() => ClaudeSessionStore.ReadTranscript(past.FilePath));
        // Claude Code resumes a conversation only from the folder it ran in.
        if (past.WorkingDirectory is { Length: > 0 } folder && Directory.Exists(folder)) Folder = folder;
        foreach (var entry in transcript)
        {
            switch (entry)
            {
                case TranscriptUserText user:
                    Add(new UserChatItem(user.Text));
                    break;
                case TranscriptAssistantText text:
                    Add(new AssistantChatItem { Text = text.Text, IsStreaming = false });
                    break;
                case TranscriptToolUse use:
                    var tool = new ToolChatItem(use.Id, use.Name, ToolChatItem.Summarize(use.Name, use.Input)) { IsRunning = false };
                    _tools[use.Id] = tool;
                    Add(tool);
                    break;
                case TranscriptToolResult result when _tools.TryGetValue(result.ToolUseId, out var t):
                    t.Complete(result.Content, result.IsError);
                    break;
            }
        }
        _tools.Clear();
        SessionId = past.SessionId;
        Title = past.Title;
        Add(new NoticeChatItem($"Continued from {past.LastActive:g}. Your next message picks up this conversation."));
        OnPropertyChanged(nameof(CanChangeFolder));
    }

    /// <summary>Ends this chat's Claude Code process (the tab is closing).</summary>
    public async Task CloseAsync()
    {
        _endingOnPurpose = true;
        ExpirePermissions();
        if (_session is { } session)
        {
            Detach();
            await _module.EndSessionAsync(session);
        }
    }

    private void Attach(ClaudeSession session)
    {
        _session = session;
        _ = Task.Run(() => PumpAsync(session));
    }

    private void Detach()
    {
        _session = null;
        _streaming = null;
    }

    /// <summary>Reads the session's events on a pool thread and applies them on the UI thread, in order.</summary>
    private async Task PumpAsync(ClaudeSession session)
    {
        try
        {
            await foreach (var ev in session.Events.ReadAllAsync().ConfigureAwait(false))
            {
                _ui.Post(() =>
                {
                    if (ReferenceEquals(session, _session)) Apply(ev);
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading Claude Code events failed");
        }
        var stderr = session.StandardErrorTail;
        _ui.Post(() => OnSessionEnded(session, stderr));
    }

    /// <summary>The user answered a permission card; send it to the CLI that asked.</summary>
    private async Task AnswerPermissionAsync(PermissionChatItem item, PermissionOption? option, bool allow)
    {
        _pendingPermissions.Remove(item);
        var session = _session;
        if (session is null)
        {
            item.Expire();
            return;
        }
        if (_pendingPermissions.Count == 0 && State == ChatState.WaitingForPermission) State = ChatState.Busy;
        try
        {
            if (allow) await session.AllowAsync(item.Request, option?.UpdatedPermissions);
            else await session.DenyAsync(item.Request, PermissionChatItem.DeniedMessage);
        }
        catch (ClaudeSessionException ex)
        {
            _logger.LogWarning(ex, "Could not answer a permission request");
            Add(new NoticeChatItem($"Helm could not pass on your answer: {ex.Message}", NoticeKind.Error));
        }
    }

    private void ExpirePermissions()
    {
        foreach (var item in _pendingPermissions) item.Expire();
        _pendingPermissions.Clear();
    }

    private void Apply(ClaudeEvent ev)
    {
        switch (ev)
        {
            case SessionStarted started:
                SessionId = started.SessionId;
                Model = started.Model;
                break;

            case TextDelta { ParentToolUseId: null } delta:
                if (_streaming is null)
                {
                    _streaming = new AssistantChatItem();
                    Add(_streaming);
                }
                _streaming.Append(delta.Text);
                break;

            case AssistantMessage { ParentToolUseId: null } message:
                ApplyAssistantMessage(message);
                break;

            case ToolResult result when _tools.TryGetValue(result.ToolUseId, out var tool):
                tool.Complete(result.Content, result.IsError);
                break;

            case PermissionRequest request:
                SealStreaming();
                var card = new PermissionChatItem(request, AnswerPermissionAsync);
                _pendingPermissions.Add(card);
                Add(card);
                if (State is ChatState.Busy) State = ChatState.WaitingForPermission;
                RaiseAttention(ChatAttention.NeedsYou);
                break;

            case PermissionDenied denied:
                Add(new NoticeChatItem($"{denied.ToolName} was not allowed: {denied.Message}", NoticeKind.Warning));
                break;

            case ConversationNotice { ParentToolUseId: null } notice:
                Add(new NoticeChatItem(notice.Text));
                break;

            case TurnCompleted done:
                SealStreaming();
                ExpirePermissions();
                foreach (var t in _tools.Values.Where(t => t.IsRunning)) t.IsRunning = false;
                SessionCostUsd = done.CostUsd;
                var failed = done.IsError && !done.WasInterrupted && State != ChatState.Stopping;
                var stopped = State == ChatState.Stopping || done.WasInterrupted;
                if (failed) Add(new NoticeChatItem($"The turn ended with an error ({done.Subtype}).", NoticeKind.Error));
                State = ChatState.Idle;
                TurnFinished?.Invoke(this, EventArgs.Empty);
                if (!stopped) RaiseAttention(failed ? ChatAttention.Failed : ChatAttention.Done); // the user pressed Stop: nothing to announce
                break;
        }
    }

    private void ApplyAssistantMessage(AssistantMessage message)
    {
        foreach (var block in message.Blocks)
        {
            switch (block)
            {
                case AssistantText text:
                    // Normally already streamed; without partial messages this is the only copy.
                    if (_streaming is null && !string.IsNullOrEmpty(text.Text)) Add(new AssistantChatItem { Text = text.Text });
                    SealStreaming();
                    break;
                case AssistantToolUse use:
                    SealStreaming();
                    var tool = new ToolChatItem(use.Id, use.Name, ToolChatItem.Summarize(use.Name, use.Input));
                    _tools[use.Id] = tool;
                    Add(tool);
                    break;
            }
        }
    }

    private void SealStreaming()
    {
        if (_streaming is null) return;
        _streaming.IsStreaming = false;
        _streaming = null;
    }

    private void OnSessionEnded(ClaudeSession session, string stderr)
    {
        if (!ReferenceEquals(session, _session)) return;
        SealStreaming(); // before Detach, which forgets the streaming row
        Detach();
        ExpirePermissions();
        if (!_endingOnPurpose)
        {
            var message = _module.IsEnabled ? "Claude Code stopped unexpectedly." : "Claude Chat was turned off, so the session ended.";
            if (_module.IsEnabled && !string.IsNullOrWhiteSpace(stderr)) message += Environment.NewLine + stderr;
            Add(new NoticeChatItem(message, _module.IsEnabled ? NoticeKind.Error : NoticeKind.Info));
        }
        var wasWorking = IsBusy;
        State = ChatState.Idle;
        if (wasWorking && !_endingOnPurpose && _module.IsEnabled) RaiseAttention(ChatAttention.Failed);
    }

    private void Add(ChatItem item)
    {
        Items.Add(item);
        if (Items.Count == 1)
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanChangeFolder));
        }
    }

    /// <summary>"/ Export conversation": the chat as Markdown (messages, tool calls, notices).</summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# ").AppendLine(Title).AppendLine();
        sb.Append("Folder: `").Append(Folder).AppendLine("`").AppendLine();
        foreach (var item in Items)
        {
            switch (item)
            {
                case UserChatItem u:
                    sb.AppendLine("## You").AppendLine().AppendLine(u.Text).AppendLine();
                    break;
                case AssistantChatItem a:
                    sb.AppendLine("## Claude").AppendLine().AppendLine(a.Text).AppendLine();
                    break;
                case ToolChatItem t:
                    sb.Append("> **").Append(t.Name).Append("** `").Append(t.Summary).AppendLine("`").AppendLine();
                    break;
                case NoticeChatItem n:
                    sb.Append("> ").AppendLine(n.Text.ReplaceLineEndings(" ")).AppendLine();
                    break;
            }
        }
        return sb.ToString();
    }

    internal static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= TitleLength ? line : line[..TitleLength].TrimEnd() + "…";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
