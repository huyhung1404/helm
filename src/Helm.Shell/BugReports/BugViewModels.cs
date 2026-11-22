using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Diagnostics;
using Helm.Core.Services;
using Helm.Shell.Services;

namespace Helm.Shell.BugReports;

/// <summary>A picture in a report, details or answer: one the user just picked, or one already saved with the bug.</summary>
public sealed class BugImageItem
{
    private readonly Func<CancellationToken, Task<byte[]>> _load;

    public BugImageItem(BugImageDraft draft)
    {
        Draft = draft;
        Name = draft.Name ?? "Image";
        _load = _ => Task.FromResult(draft.Data);
    }

    public BugImageItem(BugAttachment saved, BugStore store)
    {
        Name = saved.Name ?? "Image";
        _load = ct => store.ReadImageAsync(saved, ct);
    }

    /// <summary>Set for a picture not saved yet.</summary>
    public BugImageDraft? Draft { get; }

    public string Name { get; }

    /// <summary>The picture's bytes (a saved one may be fetched from the sync server).</summary>
    public Task<byte[]> LoadAsync(CancellationToken ct = default) => _load(ct);
}

/// <summary>One line of a black box as shown: "-37.9 s", what kind, what happened.</summary>
public sealed partial class BlackBoxEventItem(BlackBoxEvent e, double secondsBefore) : ObservableObject
{
    public BlackBoxEvent Event { get; } = e;

    public BlackBoxKind Kind => Event.Kind;

    public string Time { get; } = secondsBefore.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    public string Text => Event.Text;

    public string? Source => Event.Source;

    public string? Detail => Event.Detail;

    public bool HasDetail => !string.IsNullOrEmpty(Event.Detail);

    public bool IsError => Event.Kind == BlackBoxKind.Error;

    /// <summary>The last second before the freeze (the report itself).</summary>
    public bool IsMark { get; } = secondsBefore > -0.5;

    public string KindName => Event.Kind switch
    {
        BlackBoxKind.Input => "Input",
        BlackBoxKind.Navigation => "Navigation",
        BlackBoxKind.Log => "Log",
        BlackBoxKind.Error => "Error",
        BlackBoxKind.Performance => "Performance",
        BlackBoxKind.Sync => "Sync",
        _ => "Disabled click",
    };

    [ObservableProperty]
    private bool _showDetail;

    [RelayCommand]
    private void ToggleDetail() => ShowDetail = !ShowDetail;

    public static IReadOnlyList<BlackBoxEventItem> Of(BlackBoxSnapshot box) =>
        [.. box.Events.Select(e => new BlackBoxEventItem(e, box.SecondsBefore(e)))];
}

/// <summary>Which bugs the page lists.</summary>
public enum BugFilter
{
    NotClosed,
    Open,
    Fixing,
    ToVerify,
    Closed,
    All,
}

/// <summary>Which events of a black box are shown.</summary>
public enum BlackBoxFilter
{
    All,
    Input,
    Navigation,
    Errors,
    Log,
    Performance,
}

public static class BugText
{
    public static string Status(BugStatus status) => status switch
    {
        BugStatus.Open => "Open",
        BugStatus.Fixing => "Fixing",
        BugStatus.ToVerify => "To verify",
        _ => "Closed",
    };

    public static string Severity(BugSeverity severity) => severity switch
    {
        BugSeverity.Blocking => "Blocks me",
        BugSeverity.Annoying => "Annoying",
        _ => "Minor",
    };

    public static string Filter(BugFilter filter) => filter switch
    {
        BugFilter.NotClosed => "Not closed",
        BugFilter.Open => "Open",
        BugFilter.Fixing => "Fixing",
        BugFilter.ToVerify => "To verify",
        BugFilter.Closed => "Closed",
        _ => "All",
    };

    public static bool Matches(BugFilter filter, BugStatus status) => filter switch
    {
        BugFilter.NotClosed => status != BugStatus.Closed,
        BugFilter.Open => status == BugStatus.Open,
        BugFilter.Fixing => status == BugStatus.Fixing,
        BugFilter.ToVerify => status == BugStatus.ToVerify,
        BugFilter.Closed => status == BugStatus.Closed,
        _ => true,
    };

    public static bool Matches(BlackBoxFilter filter, BlackBoxKind kind) => filter switch
    {
        BlackBoxFilter.All => true,
        BlackBoxFilter.Input => kind is BlackBoxKind.Input or BlackBoxKind.DisabledClick,
        BlackBoxFilter.Navigation => kind == BlackBoxKind.Navigation,
        BlackBoxFilter.Errors => kind is BlackBoxKind.Error or BlackBoxKind.DisabledClick,
        BlackBoxFilter.Log => kind is BlackBoxKind.Log or BlackBoxKind.Sync,
        _ => kind == BlackBoxKind.Performance,
    };

    /// <summary>"3 min", "5 h", "2 d", "18 Nov".</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} d";
        return at.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>"Today 14:32", "Yesterday 20:14", "18 Nov 09:10".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var day = local.Date == today ? "Today" : local.Date == today.AddDays(-1) ? "Yesterday" : local.ToString("d MMM", CultureInfo.InvariantCulture);
        return $"{day} {local:HH:mm}";
    }

    /// <summary>One line about a black box: "214 events · 1 error · froze 1.8 s".</summary>
    public static string Summary(BlackBoxSnapshot box)
    {
        var parts = new List<string> { $"{box.Events.Count} events" };
        if (box.ErrorCount > 0) parts.Add(box.ErrorCount == 1 ? "1 error" : $"{box.ErrorCount} errors");
        if (box.LongestFreeze is { } freeze) parts.Add($"froze {freeze.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        if (box.DisabledClickCount > 0) parts.Add(box.DisabledClickCount == 1 ? "1 click on a disabled button" : $"{box.DisabledClickCount} clicks on disabled buttons");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// The report window: what happened, how bad, which tool, the pictures the user adds, and the black box frozen when
/// the window opened. <see cref="Close"/> thaws the black box (sent or not).
/// </summary>
public sealed partial class BugReportFormViewModel : ObservableObject
{
    private readonly BugStore _store;
    private readonly BlackBox _box;
    private bool _closed;

    public BugReportFormViewModel(BugStore store, BlackBox box, BlackBoxSnapshot snapshot, string? tool, IReadOnlyList<string> tools, bool fromCrash = false)
    {
        _store = store;
        _box = box;
        Snapshot = snapshot;
        FromCrash = fromCrash;
        Tools = [.. tools.Append("Helm").Distinct(StringComparer.OrdinalIgnoreCase)];
        _tool = tool is { Length: > 0 } t && Tools.Contains(t, StringComparer.OrdinalIgnoreCase) ? Tools.First(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)) : "Helm";
        Events = BlackBoxEventItem.Of(snapshot);
        Images.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasImages));
    }

    public BlackBoxSnapshot Snapshot { get; }

    public bool FromCrash { get; }

    public IReadOnlyList<string> Tools { get; }

    public IReadOnlyList<BlackBoxEventItem> Events { get; }

    public string FrozenAt => Snapshot.FrozenAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"The last 60 s, frozen at 14:31:52 · 214 events" (errors and freezes are tags beside it).</summary>
    public string BlackBoxText => $"The last {(int)BlackBox.Window.TotalSeconds} s, frozen at {FrozenAt} · {Snapshot.Events.Count} events";

    public bool HasErrors => ErrorCount > 0;

    public string ErrorText => ErrorCount == 1 ? "1 error" : $"{ErrorCount} errors";

    public int ErrorCount => Snapshot.ErrorCount;

    public string? FreezeText => Snapshot.LongestFreeze is { } f ? $"Froze {f.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s" : null;

    public ObservableCollection<BugImageItem> Images { get; } = [];

    public bool HasImages => Images.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlocking), nameof(IsAnnoying), nameof(IsMinor))]
    private BugSeverity _severity = BugSeverity.Annoying;

    public bool IsBlocking => Severity == BugSeverity.Blocking;

    public bool IsAnnoying => Severity == BugSeverity.Annoying;

    public bool IsMinor => Severity == BugSeverity.Minor;

    [ObservableProperty]
    private string _tool;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    /// <summary>Sent: the window shows "Bug #B13 saved".</summary>
    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private string? _sentCode;

    [ObservableProperty]
    private string? _error;

    /// <summary>The id of the bug once sent.</summary>
    public string? SentId { get; private set; }

    /// <summary>Adds a picture (from the button, a paste, a drop, the phone's gallery or camera).</summary>
    public bool AddImage(BugImageDraft image)
    {
        Error = null;
        if (Images.Count >= BugStore.MaxImages) { Error = $"At most {BugStore.MaxImages} images."; return false; }
        if (image.Data.Length > BugStore.MaxImageBytes) { Error = $"{image.Name ?? "That image"} is larger than {BugStore.MaxImageBytes / (1024 * 1024)} MB."; return false; }
        Images.Add(new BugImageItem(image));
        return true;
    }

    [RelayCommand]
    private void RemoveImage(BugImageItem image) => Images.Remove(image);

    [RelayCommand]
    private void SetSeverity(BugSeverity severity) => Severity = severity;

    private bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        IsSending = true;
        Error = null;
        try
        {
            SentId = await _store.ReportAsync(new BugDraft(Text, Severity, Tool, [.. Images.Select(i => i.Draft!)], Snapshot, FromCrash));
            SentCode = _store.Get(SentId)?.Code;
            IsDone = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>The window closed (sent or cancelled): the black box records again.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _box.Thaw();
    }
}

/// <summary>A bug in the page's list.</summary>
public sealed class BugRowViewModel(string id, BugReport bug, int replies, DateTimeOffset now)
{
    public string Id { get; } = id;

    public BugReport Bug { get; } = bug;

    public string Code => "#" + Bug.Code;

    public string Title => Bug.Title;

    public BugStatus Status => Bug.Status;

    public string StatusText => BugText.Status(Bug.Status);

    public BugSeverity Severity => Bug.Severity;

    public string SeverityText => BugText.Severity(Bug.Severity);

    public string Tool => Bug.Tool;

    public string Device => Bug.Device;

    public bool OnPhone => Bug.DeviceKind == BugDeviceKind.Phone;

    public string Ago { get; } = BugText.Ago(bug.CreatedAt, now);

    public int Replies { get; } = replies;

    public bool HasReplies => Replies > 0;

    public string RepliesText => Replies == 1 ? "1 reply" : $"{Replies} replies";

    public bool IsAsking => Bug.PendingQuestion is not null;

    /// <summary>"Tracker · Home PC · 3 min".</summary>
    public string Meta => $"{Tool} · {Device} · {Ago}";
}

/// <summary>One entry of a bug's conversation as shown.</summary>
public sealed class BugEntryViewModel(string id, BugEntry entry, BugStore store, DateTimeOffset now)
{
    public string Id { get; } = id;

    public BugEntry Entry { get; } = entry;

    public bool IsStatus => Entry.Kind == BugEntryKind.Status;

    public bool IsMessage => !IsStatus;

    public bool ByAgent => Entry.ByAgent;

    public bool IsQuestion => Entry.Kind == BugEntryKind.Question;

    /// <summary>"question" or "answer" beside the author, else null.</summary>
    public string? Badge => Entry.Kind switch { BugEntryKind.Question => "question", BugEntryKind.Answer => "answer", _ => null };

    public string Author => Entry.Author;

    /// <summary>"H" for the user, the agent's first letter otherwise.</summary>
    public string Initial => Entry.ByAgent ? (Entry.Author.Length > 0 ? Entry.Author[..1] : "A") : "H";

    public string When { get; } = $"{BugText.When(entry.At, now)} · {entry.Device}";

    public string Text => Entry.Text;

    public bool HasBlackBox => Entry.BlackBox is not null;

    public string BlackBoxText => $"Black box · {Entry.BlackBoxEvents} events" + (Entry.BlackBoxErrors > 0 ? $" · {Entry.BlackBoxErrors} error{(Entry.BlackBoxErrors == 1 ? "" : "s")}" : "");

    public IReadOnlyList<BugImageItem> Images { get; } = [.. entry.Images.Select(i => new BugImageItem(i, store))];

    public bool HasImages => Images.Count > 0;
}

/// <summary>One bug opened on the page: its conversation, black box, environment and what can be done with it.</summary>
public sealed partial class BugDetailViewModel : ObservableObject
{
    private readonly BugStore _store;
    private readonly BlackBox _box;
    private readonly BugHandOff _handOff;
    private readonly IClipboardService? _clipboard;
    private readonly TimeProvider _time;
    private BlackBoxSnapshot? _snapshot;

    public BugDetailViewModel(string id, BugStore store, BlackBox box, BugHandOff handOff, IClipboardService? clipboard, TimeProvider time)
    {
        Id = id;
        _store = store;
        _box = box;
        _handOff = handOff;
        _clipboard = clipboard;
        _time = time;
        _bug = store.Get(id) ?? new BugReport();
        Reload();
        DetailImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDetailImages));
        AnswerImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAnswerImages));
    }

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Code), nameof(Title), nameof(StatusText), nameof(SeverityText), nameof(Meta), nameof(IsOpen), nameof(IsFixing),
        nameof(IsToVerify), nameof(IsClosed), nameof(IsAsking), nameof(Question), nameof(FixingText), nameof(VerifyText), nameof(ClosedText), nameof(CanAddDetails))]
    private BugReport _bug;

    public string Code => "#" + Bug.Code;

    public string Title => Bug.Title;

    public string StatusText => BugText.Status(Bug.Status);

    public string SeverityText => BugText.Severity(Bug.Severity);

    public string Meta => $"{Bug.Tool} · {Bug.Device} · {BugText.When(Bug.CreatedAt, _time.GetUtcNow())} · Helm {Bug.AppVersion}";

    public bool IsOpen => Bug.Status == BugStatus.Open;

    public bool IsFixing => Bug.Status == BugStatus.Fixing;

    public bool IsToVerify => Bug.Status == BugStatus.ToVerify;

    public bool IsClosed => Bug.Status == BugStatus.Closed;

    public bool CanAddDetails => !IsClosed;

    public bool IsAsking => Bug.PendingQuestion is not null;

    public string? Question => Bug.PendingQuestion is { } q ? _store.GetEntry(q)?.Text : null;

    public string FixingText => $"{Bug.FixingBy ?? "An agent"} is fixing it" + (Bug.Branch is { } b ? $" · {b}" : "");

    public string VerifyText => "Waiting for you to check" + (Bug.FixedIn is { } v ? $" · fixed in {v}" : "") + (Bug.Resolution is { } r ? $"\n{r}" : "");

    public string ClosedText => Bug.CloseReason switch
    {
        BugCloseReason.Fixed => "Closed: fixed" + (Bug.FixedIn is { } v ? $" in {v}" : ""),
        BugCloseReason.NotABug => "Closed: not a bug",
        BugCloseReason.Duplicate => $"Closed: duplicate of {Bug.DuplicateOf}",
        _ => "Closed",
    };

    public ObservableCollection<BugEntryViewModel> Entries { get; } = [];

    // ---- black box ----

    [ObservableProperty]
    private bool _hasBlackBox;

    [ObservableProperty]
    private string _blackBoxText = "";

    [ObservableProperty]
    private IReadOnlyList<BlackBoxEventItem> _events = [];

    [ObservableProperty]
    private IReadOnlyList<string> _steps = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentLines))]
    private IReadOnlyList<KeyValuePair<string, string>> _environment = [];

    /// <summary>"Helm: 0.85.2" lines (the phone shows them as text).</summary>
    public IReadOnlyList<string> EnvironmentLines => [.. Environment.Select(kv => $"{kv.Key}: {kv.Value}")];

    [ObservableProperty]
    private BlackBoxFilter _eventFilter;

    [ObservableProperty]
    private bool _isLoadingBlackBox;

    partial void OnEventFilterChanged(BlackBoxFilter value) => ShowEvents();

    [RelayCommand]
    private void SetEventFilter(BlackBoxFilter filter) => EventFilter = filter;

    /// <summary>Reads the newest black box (from the sync server if this device does not have it yet).</summary>
    public async Task LoadBlackBoxAsync(CancellationToken ct = default)
    {
        if (_store.LatestBlackBox(Id) is not { } entry) { HasBlackBox = false; return; }
        IsLoadingBlackBox = true;
        try
        {
            _snapshot = await _store.ReadBlackBoxAsync(entry.Value, ct);
            if (_snapshot is null) return;
            HasBlackBox = true;
            BlackBoxText = $"The {(int)BlackBox.Window.TotalSeconds} s before {(entry.Value.Kind == BugEntryKind.Report ? "the report" : "the details")} · {BugText.Summary(_snapshot)}";
            Steps = [.. _snapshot.Events.Where(e => e.Kind is BlackBoxKind.Input or BlackBoxKind.Navigation).Select(e => e.Text).TakeLast(12)];
            Environment = [.. _snapshot.Context];
            ShowEvents();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Helm.Core.Sync.BlobUnavailableException or System.Text.Json.JsonException or HttpRequestException)
        {
            BlackBoxText = $"The black box cannot be read now: {ex.Message}";
        }
        finally
        {
            IsLoadingBlackBox = false;
        }
    }

    private void ShowEvents() =>
        Events = _snapshot is null ? [] : [.. BlackBoxEventItem.Of(_snapshot).Where(e => BugText.Matches(EventFilter, e.Kind))];

    // ---- adding details, answering ----

    [ObservableProperty]
    private string _detailsText = "";

    [ObservableProperty]
    private bool _attachBlackBox;

    public ObservableCollection<BugImageItem> DetailImages { get; } = [];

    public bool HasDetailImages => DetailImages.Count > 0;

    [ObservableProperty]
    private string _answerText = "";

    [ObservableProperty]
    private bool _answerWithBlackBox;

    public ObservableCollection<BugImageItem> AnswerImages { get; } = [];

    public bool HasAnswerImages => AnswerImages.Count > 0;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>A short message after an action ("Details added", or what went wrong).</summary>
    [ObservableProperty]
    private string? _status;

    public void AddDetailImage(BugImageDraft image) => AddTo(DetailImages, image);

    public void AddAnswerImage(BugImageDraft image) => AddTo(AnswerImages, image);

    [RelayCommand]
    private void RemoveDetailImage(BugImageItem image) => DetailImages.Remove(image);

    [RelayCommand]
    private void RemoveAnswerImage(BugImageItem image) => AnswerImages.Remove(image);

    [RelayCommand]
    private Task AddDetailsAsync() => RunAsync(async () =>
    {
        await _store.AddDetailsAsync(Id, DetailsText, Drafts(DetailImages), AttachBlackBox ? _box.Snapshot() : null);
        DetailsText = "";
        DetailImages.Clear();
        AttachBlackBox = false;
        return "Details added";
    });

    [RelayCommand]
    private Task AnswerAsync() => RunAsync(async () =>
    {
        await _store.AnswerAsync(Id, AnswerText, Drafts(AnswerImages), AnswerWithBlackBox ? _box.Snapshot() : null);
        AnswerText = "";
        AnswerImages.Clear();
        AnswerWithBlackBox = false;
        return "Answer sent";
    });

    // ---- status ----

    [RelayCommand]
    private Task ConfirmFixedAsync() => RunAsync(() => { _store.ConfirmFixed(Id); return Task.FromResult($"{Code} closed"); });

    [RelayCommand]
    private Task TakeBackAsync() => RunAsync(() => { _store.TakeBack(Id); return Task.FromResult($"{Code} is open again"); });

    /// <summary>After the platform's "Close this bug" dialog.</summary>
    public Task CloseAsync(BugCloseReason reason, string? note, string? duplicateOf = null) =>
        RunAsync(() => { _store.Close(Id, reason, note, duplicateOf); return Task.FromResult($"{Code} closed"); });

    /// <summary>After the platform's "Reopen" dialog.</summary>
    public Task ReopenAsync(string why, bool withBlackBox) =>
        RunAsync(async () => { await _store.ReopenAsync(Id, why, [], withBlackBox ? _box.Snapshot() : null); return $"{Code} reopened"; });

    // ---- Claude Code ----

    public bool CanHandOff => _handOff.IsAvailable;

    public string HandOffPrompt => BugHandOff.Prompt(Bug);

    public Task<IReadOnlyList<HandOffTarget>> HandOffTargetsAsync() => _handOff.TargetsAsync();

    /// <summary>After the platform's "Hand to Claude Code" dialog.</summary>
    public Task HandOffAsync(HandOffTarget target) => RunAsync(async () =>
    {
        var sentTo = await _handOff.SendAsync(target, HandOffPrompt);
        _store.HandOver(Id, target.NewSession ? $"Claude Code · {target.Project}" : $"Claude Code · {target.Label}");
        return $"Sent {Code} to {sentTo}";
    });

    /// <summary>The bug and its black box as Markdown, for pasting into any AI chat.</summary>
    [RelayCommand]
    private void CopyForClaude()
    {
        var md = new StringBuilder();
        md.AppendLine($"# Helm bug {Code}: {Bug.Title}");
        md.AppendLine($"{BugText.Status(Bug.Status)} · {BugText.Severity(Bug.Severity)} · {Bug.Tool} · {Bug.Device} · Helm {Bug.AppVersion}");
        md.AppendLine();
        foreach (var e in Entries)
            md.AppendLine(e.IsStatus ? $"- _{e.Text}_ ({e.When})" : $"**{e.Author}** ({e.When}): {e.Text}" + (e.HasImages ? $" [{e.Images.Count} image(s)]" : ""));
        if (_snapshot is { } box)
        {
            md.AppendLine();
            md.AppendLine($"## Black box ({BugText.Summary(box)})");
            md.AppendLine("```");
            foreach (var e in box.Events)
                md.AppendLine($"{box.SecondsBefore(e),7:0.0}s {e.Kind,-13} {e.Text}" + (e.Source is { } s ? $" [{s}]" : ""));
            md.AppendLine("```");
            foreach (var e in box.Events.Where(e => e.Kind == BlackBoxKind.Error && e.Detail is not null))
            {
                md.AppendLine("```");
                md.AppendLine(BlackBox.Clip(e.Detail!, 3000));
                md.AppendLine("```");
            }
        }
        md.AppendLine();
        md.AppendLine($"With Helm's MCP: bug_get {Bug.Code}.");
        _clipboard?.SetText(md.ToString());
        Status = "Copied for Claude Code";
    }

    /// <summary>Reads the bug and its conversation again (after a change here or by sync).</summary>
    public void Reload()
    {
        if (_store.Get(Id) is { } bug) Bug = bug;
        var now = _time.GetUtcNow();
        var entries = _store.Entries(Id);
        if (entries.Count == Entries.Count && entries.Select(e => e.Id).SequenceEqual(Entries.Select(e => e.Id))) return;
        Entries.Clear();
        foreach (var e in entries) Entries.Add(new BugEntryViewModel(e.Id, e.Value, _store, now));
        _ = LoadIfNewerAsync();
    }

    private string? _loadedBox;

    private async Task LoadIfNewerAsync()
    {
        var latest = _store.LatestBlackBox(Id)?.Value.BlackBox?.Id;
        if (latest == _loadedBox) return;
        _loadedBox = latest;
        await LoadBlackBoxAsync();
    }

    private async Task RunAsync(Func<Task<string>> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = null;
        try
        {
            Status = await action();
            Reload();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or Helm.Core.Mcp.McpToolException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddTo(ObservableCollection<BugImageItem> list, BugImageDraft image)
    {
        if (list.Count >= BugStore.MaxImages) { Status = $"At most {BugStore.MaxImages} images."; return; }
        if (image.Data.Length > BugStore.MaxImageBytes) { Status = $"{image.Name ?? "That image"} is larger than {BugStore.MaxImageBytes / (1024 * 1024)} MB."; return; }
        list.Add(new BugImageItem(image));
    }

    private static IReadOnlyList<BugImageDraft> Drafts(IEnumerable<BugImageItem> items) => [.. items.Select(i => i.Draft).OfType<BugImageDraft>()];
}

/// <summary>
/// The Bug reports page (next to What's new on PC, under General on the phone): the list with its filters and search,
/// and the bug opened beside it (PC) or over it (phone).
/// </summary>
public sealed partial class BugReportsViewModel : ObservableObject
{
    private readonly BugStore _store;
    private readonly BlackBox _box;
    private readonly BugHandOff _handOff;
    private readonly IClipboardService? _clipboard;
    private readonly IUiDispatcher? _ui;
    private readonly TimeProvider _time;
    private int _refreshQueued;

    public BugReportsViewModel(BugStore store, BlackBox box, BugHandOff handOff, IClipboardService? clipboard = null, IUiDispatcher? ui = null, TimeProvider? time = null)
    {
        _store = store;
        _box = box;
        _handOff = handOff;
        _clipboard = clipboard;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        _store.Changed += (_, _) => QueueRefresh();
        Refresh();
    }

    public BugStore Store => _store;

    public IReadOnlyList<BugFilter> Filters { get; } = Enum.GetValues<BugFilter>();

    public ObservableCollection<BugRowViewModel> Bugs { get; } = [];

    /// <summary>"Not closed (4)" etc., in <see cref="Filters"/>' order.</summary>
    [ObservableProperty]
    private IReadOnlyList<FilterCount> _filterCounts = [];

    [ObservableProperty]
    private BugFilter _filter = BugFilter.NotClosed;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private int _openCount;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private BugRowViewModel? _selectedRow;

    [ObservableProperty]
    private BugDetailViewModel? _selected;

    partial void OnFilterChanged(BugFilter value) => Refresh();

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnSelectedRowChanged(BugRowViewModel? value)
    {
        if (value is null) return;
        if (Selected?.Id == value.Id) return;
        Selected = new BugDetailViewModel(value.Id, _store, _box, _handOff, _clipboard, _time);
    }

    [RelayCommand]
    private void SetFilter(BugFilter filter) => Filter = filter;

    /// <summary>Opens a bug from the list (the phone's rows are buttons).</summary>
    [RelayCommand]
    private void Select(BugRowViewModel row) => SelectedRow = row;

    /// <summary>Opens a bug by its id (from a notification, the report window, a link).</summary>
    public void Open(string id)
    {
        if (_store.Get(id) is null) return;
        if (Bugs.FirstOrDefault(b => b.Id == id) is null) { Filter = BugFilter.All; Query = ""; }
        SelectedRow = Bugs.FirstOrDefault(b => b.Id == id);
        if (SelectedRow is null) Selected = new BugDetailViewModel(id, _store, _box, _handOff, _clipboard, _time);
    }

    /// <summary>Back to the list (the phone shows one at a time).</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        SelectedRow = null;
        Selected = null;
    }

    public void Refresh()
    {
        var now = _time.GetUtcNow();
        var all = _store.All();
        var q = Query.Trim();
        var shown = all.Where(b => BugText.Matches(Filter, b.Value.Status))
            .Where(b => q.Length == 0 || $"{b.Value.Code} #{b.Value.Code} {b.Value.Title} {b.Value.Description} {b.Value.Tool}".Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selectedId = SelectedRow?.Id ?? Selected?.Id;
        Bugs.Clear();
        foreach (var b in shown)
            Bugs.Add(new BugRowViewModel(b.Id, b.Value, _store.Entries(b.Id).Count(e => e.Value.Kind != BugEntryKind.Report && e.Value.Kind != BugEntryKind.Status), now));
        FilterCounts = [.. Filters.Select(f => new FilterCount(f, all.Count(b => BugText.Matches(f, b.Value.Status)), f == Filter))];
        OpenCount = all.Count(b => b.Value.Status != BugStatus.Closed);
        IsEmpty = Bugs.Count == 0;
        if (selectedId is not null)
        {
            // Keep the opened bug (and its row) across a refresh.
            SelectedRow = Bugs.FirstOrDefault(b => b.Id == selectedId);
            Selected?.Reload();
        }
    }

    private void QueueRefresh()
    {
        if (_ui is null) { Refresh(); return; }
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Refresh();
        });
    }
}

/// <summary>A filter and how many bugs it holds: "Fixing (2)".</summary>
public sealed record FilterCount(BugFilter Filter, int Count, bool IsSelected = false)
{
    public string Text => $"{BugText.Filter(Filter)} ({Count})";
}
