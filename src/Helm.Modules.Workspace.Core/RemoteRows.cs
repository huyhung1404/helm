using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Modules.Workspace;

/// <summary>A row that can want your attention while scrolled out of sight (the tab strips' red edge counts these).</summary>
public interface IAttentionItem
{
    int AttentionCount { get; }
}

/// <summary>A PC in the picker.</summary>
public sealed class HostChoice(WorkspaceHostRecord host, bool online)
{
    public WorkspaceHostRecord Host { get; } = host;
    public string MachineId => Host.MachineId;
    public bool IsOnline { get; } = online;
    public string Label => IsOnline ? Host.MachineName : $"{Host.MachineName} (offline)";
    public string Detail => IsOnline ? $"Online · {Host.Windows.Count} VS Code window{(Host.Windows.Count == 1 ? "" : "s")}" : $"Offline since {Host.LastSeen.ToLocalTime():dd/MM HH:mm}";
}

/// <summary>A VS Code window: one project tab.</summary>
public sealed class ProjectTab(HostWindowInfo window, int attention, bool working, bool selected) : IAttentionItem
{
    public HostWindowInfo Window { get; } = window;
    public string Name => Window.Name.Length > 0 ? Window.Name : "Window";
    public string Folder => Window.Folder;

    /// <summary>Sessions that finished (unseen) or wait for you.</summary>
    public int Attention { get; } = attention;
    public bool HasAttention => Attention > 0;
    public bool IsWorking { get; } = working;
    public bool IsSelected { get; } = selected;
    public int AttentionCount => Attention;
}

/// <summary>A Claude session open in the selected window: one subtab.</summary>
public sealed class SessionTab(WorkspaceSessionRecord record, bool unseen, bool selected) : IAttentionItem
{
    public WorkspaceSessionRecord Record { get; } = record;
    public string SessionId => Record.SessionId;
    public string Title => Record.Title.Length > 0 ? Record.Title : Record.LastPrompt is { Length: > 0 } p ? ClaudeHistory.Shorten(p, 40) : "New session";
    public bool IsWorking => Record.State == SessionState.Working;
    public bool NeedsYou => Record.State is SessionState.NeedsPermission or SessionState.Question;
    public bool Unseen { get; } = unseen;
    public bool IsSelected { get; } = selected;

    /// <summary>A finished tab can be closed from the phone (it stays in History).</summary>
    public bool CanClose => IsSelected && Record.State is SessionState.Done or SessionState.Idle;

    public int AttentionCount => NeedsYou || Unseen && !IsSelected ? 1 : 0;
}

public enum TimelineKind { User, Thought, Tool, Answer, Working }

/// <summary>One line of the open conversation's timeline.</summary>
public sealed class TimelineRow
{
    public TimelineRow(TranscriptItem item)
    {
        Item = item;
        Kind = item.Kind switch
        {
            TranscriptItemKind.User => TimelineKind.User,
            TranscriptItemKind.Thought => TimelineKind.Thought,
            TranscriptItemKind.Tool => TimelineKind.Tool,
            _ => TimelineKind.Answer,
        };
    }

    private TimelineRow(TimelineKind kind, string text)
    {
        Item = new TranscriptItem { Text = text };
        Kind = kind;
    }

    /// <summary>"✻ Working…" under the timeline while Claude thinks between steps.</summary>
    public static TimelineRow Working(string text) => new(TimelineKind.Working, text);

    /// <summary>Your prompt on its way (not in the transcript yet), with how far it got.</summary>
    public static TimelineRow Sending(string text, string status, bool failed) =>
        new(new TranscriptItem { Kind = TranscriptItemKind.User, Text = text, At = DateTimeOffset.Now }) { Delivery = status, DeliveryFailed = failed };

    public TranscriptItem Item { get; }
    public TimelineKind Kind { get; }
    public bool IsUser => Kind == TimelineKind.User;
    public bool IsThought => Kind == TimelineKind.Thought;
    public bool IsTool => Kind == TimelineKind.Tool;
    public bool IsAnswer => Kind == TimelineKind.Answer;
    public bool IsWorking => Kind == TimelineKind.Working;

    public string Text => Item.Text;
    public string Time => Item.At?.ToLocalTime().ToString("HH:mm") ?? "";
    public bool HasTime => Item.At is not null && (IsUser || IsAnswer);

    public string ToolName => Item.Tool ?? "";
    public bool HasDescription => IsTool && Item.Text.Length > 0;
    public string? Input => Item.Input;
    public bool HasInput => !string.IsNullOrEmpty(Item.Input);
    public string? Output => Item.Output;
    public bool HasOutput => !string.IsNullOrEmpty(Item.Output);
    public bool Running => Item.Running;
    public bool Failed => Item.Failed;
    public bool ShowRunning => IsTool && Item.Running;
    public bool HasIo => HasInput || HasOutput || ShowRunning;

    private IReadOnlyList<MarkdownBlock>? _blocks;

    /// <summary>The answer as Markdown blocks, parsed the first time the row is drawn.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks => _blocks ??= Kind == TimelineKind.Answer ? MarkdownBlocks.Parse(Item.Text) : [];

    /// <summary>Your prompt from the phone: "✓ Claude received it" and the like; null for one from the transcript.</summary>
    public string? Delivery { get; init; }
    public bool DeliveryFailed { get; init; }
    public bool HasDelivery => Delivery is not null;

    public bool Same(TimelineRow other) => Kind == other.Kind && Item == other.Item && Delivery == other.Delivery && DeliveryFailed == other.DeliveryFailed;
}

/// <summary>A model in the model sheet.</summary>
public sealed class ModelOption(ModelInfo model, bool current)
{
    public ModelInfo Model { get; } = model;
    public string Name => Model.DisplayName;
    public string Description => Model.Description;
    public bool IsCurrent { get; } = current;
}

/// <summary>A permission mode in the mode sheet.</summary>
public sealed class ModeOption(string mode, bool current)
{
    public string Mode { get; } = mode;
    public string Name => PermissionModes.Label(Mode);
    public string Description => Mode switch
    {
        PermissionModes.Manual => "Claude will ask for approval before making each edit",
        PermissionModes.EditAutomatically => "Claude will edit your selected text or the whole file",
        PermissionModes.Plan => "Claude will explore the code and present a plan before editing",
        PermissionModes.Auto => "Claude will approve actions that pass a safety check and pause for anything risky",
        _ => "",
    };
    public bool IsCurrent { get; } = current;
    public bool IsManual => Mode == PermissionModes.Manual;
    public bool IsEdits => Mode == PermissionModes.EditAutomatically;
    public bool IsPlan => Mode == PermissionModes.Plan;
    public bool IsAuto => Mode == PermissionModes.Auto;
}

/// <summary>One of the five effort dots.</summary>
public sealed class EffortDot(string level, bool current, bool available)
{
    public string Level { get; } = level;
    public string Label => PermissionModes.EffortLabel(Level);
    public bool IsCurrent { get; } = current;
    public bool IsAvailable { get; } = available;
}

/// <summary>A subagent at work, in the agents sheet.</summary>
public sealed class AgentRow(SubagentInfo agent, DateTimeOffset now)
{
    public string Type => agent.Type.Length > 0 ? agent.Type : "Agent";
    public string Task => agent.Task;
    public string? Doing => agent.Doing;
    public bool HasDoing => !string.IsNullOrEmpty(agent.Doing);
    public string Elapsed => agent.Since is { } since ? Format(now - since) : "";

    internal static string Format(TimeSpan t) =>
        t < TimeSpan.Zero ? "" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
}

/// <summary>A line of the History sheet: a day header, a session open in VS Code, or an earlier one.</summary>
public sealed class HistoryRow
{
    public string? Day { get; init; }
    public bool IsDay => Day is not null;
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string When { get; init; } = "";
    public bool IsOpen { get; init; }
    public bool IsSession => !IsDay;
    public bool NeedsYou { get; init; }
    public bool IsWorking { get; init; }
}

/// <summary>One choice of a question; tap to pick it (several for a multi-select question).</summary>
public sealed partial class OptionChoice(QuestionOption option) : ObservableObject
{
    [ObservableProperty] private bool _isPicked;

    public string Label => option.Label;
    public string Description => option.Description;
    public bool HasDescription => option.Description.Length > 0;
}

/// <summary>One of Claude's questions, with its choices and room for your own words.</summary>
public sealed partial class QuestionChoice : ObservableObject
{
    [ObservableProperty] private string _other = "";

    public QuestionChoice(QuestionInfo question)
    {
        Info = question;
        Options = question.Options.Select(o => new OptionChoice(o)).ToList();
        foreach (var o in Options) o.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OptionChoice.IsPicked) || !o.IsPicked || question.MultiSelect) return;
            // One answer: picking one drops the other.
            foreach (var other in Options.Where(x => x != o)) other.IsPicked = false;
        };
    }

    public QuestionInfo Info { get; }
    public string Question => Info.Question;
    public string Header => Info.Header;
    public bool MultiSelect => Info.MultiSelect;
    public IReadOnlyList<OptionChoice> Options { get; }

    /// <summary>Your own words win; else the picked labels (comma-separated when several); null when nothing yet.</summary>
    public string? Answer => Other.Trim() is { Length: > 0 } own
        ? own
        : Options.Where(o => o.IsPicked).Select(o => o.Label).ToList() is { Count: > 0 } picked ? string.Join(", ", picked) : null;
}
