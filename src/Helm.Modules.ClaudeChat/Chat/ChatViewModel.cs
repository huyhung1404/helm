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

/// <summary>
/// The conversation: its rows, the draft, and the session feeding it. It does not know where it is shown, so the
/// same instance keeps running when the view moves between the module page and a floating window.
/// All members are UI-thread only; session events are marshalled through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly ClaudeChatModule _module;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly Dictionary<string, ToolChatItem> _tools = new(StringComparer.Ordinal);
    private readonly List<PermissionChatItem> _pendingPermissions = [];
    private ClaudeSession? _session;
    private AssistantChatItem? _streaming;
    private bool _endingOnPurpose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanSend), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(NewChatCommand))]
    private ChatState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _model;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _workingDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CostText))]
    private double _sessionCostUsd;

    public ChatViewModel(ClaudeChatModule module, IUiDispatcher ui, ILogger logger)
    {
        _module = module;
        _ui = ui;
        _logger = logger;
    }

    public ObservableCollection<ChatItem> Items { get; } = [];

    /// <summary>The CLI's id for this conversation (for resuming it later).</summary>
    public string? SessionId { get; private set; }

    public bool IsBusy => State is ChatState.Starting or ChatState.Busy or ChatState.WaitingForPermission or ChatState.Stopping;

    public bool CanSend => State == ChatState.Idle && !string.IsNullOrWhiteSpace(Draft);

    public bool IsEmpty => Items.Count == 0;

    public string StatusText => State switch
    {
        ChatState.Starting => "Starting Claude Code…",
        ChatState.Busy => "Claude is working…",
        ChatState.WaitingForPermission => "Waiting for your answer…",
        ChatState.Stopping => "Stopping…",
        _ when _session is null => "Ready — a session starts with your first message",
        _ => string.Join(" · ", new[] { Model, WorkingDirectory }.Where(s => !string.IsNullOrEmpty(s))),
    };

    /// <summary>What the turns would cost at API prices; with a Claude subscription nothing is charged per token.</summary>
    public string? CostText => SessionCostUsd > 0 ? string.Create(CultureInfo.InvariantCulture, $"≈ ${SessionCostUsd:0.000} API-equivalent") : null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0 || State != ChatState.Idle) return;
        Draft = string.Empty;
        Add(new UserChatItem(text));

        try
        {
            if (_session is null || _session.HasExited)
            {
                State = ChatState.Starting;
                var session = await _module.StartSessionAsync(SessionId, CancellationToken.None);
                Attach(session);
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

    private bool CanStartNewChat => State is ChatState.Idle;

    /// <summary>Ends the Claude Code session and clears the conversation.</summary>
    [RelayCommand(CanExecute = nameof(CanStartNewChat))]
    private async Task NewChatAsync()
    {
        _endingOnPurpose = true;
        Detach();
        await _module.EndSessionAsync();
        _endingOnPurpose = false;
        ExpirePermissions();
        Items.Clear();
        _tools.Clear();
        SessionId = null;
        Model = null;
        WorkingDirectory = null;
        SessionCostUsd = 0;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StatusText));
    }

    private void Attach(ClaudeSession session)
    {
        _session = session;
        OnPropertyChanged(nameof(StatusText));
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
                WorkingDirectory = started.WorkingDirectory;
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
                if (done.IsError && !done.WasInterrupted && State != ChatState.Stopping) Add(new NoticeChatItem($"The turn ended with an error ({done.Subtype}).", NoticeKind.Error));
                State = ChatState.Idle;
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
        State = ChatState.Idle;
        OnPropertyChanged(nameof(StatusText));
    }

    private void Add(ChatItem item)
    {
        Items.Add(item);
        if (Items.Count == 1) OnPropertyChanged(nameof(IsEmpty));
    }
}
