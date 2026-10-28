using Helm.Core.Sync;

namespace Helm.Modules.Workspace;

/// <summary>A command the phone wrote, as its card remembers it.</summary>
internal sealed record SentCommand(string CommandId, WorkspaceCommandKind Kind, string? SessionId, string? Text, DateTimeOffset At, bool Done = false, bool Failed = false)
{
    public IReadOnlyList<WorkspaceImage> Images { get; init; } = [];
}

/// <summary>One command on its way and what its card shows: the steps the PC reported, its status line, when it answered.</summary>
internal sealed class Delivery(SentCommand sent, string? folder, string? title, string? awaitSession)
{
    public SentCommand Sent { get; set; } = sent;

    /// <summary>The project (VS Code window folder) it was sent from.</summary>
    public string? Folder { get; } = folder;

    public string? Title { get; } = title;

    /// <summary>The new or reopened session whose tab should open here.</summary>
    public string? AwaitSession { get; set; } = awaitSession;

    public bool AwaitGaveUp { get; set; }
    public List<string> PcSteps { get; } = [];

    /// <summary>When the phone first saw each step (by its place in the list).</summary>
    public List<DateTimeOffset> StepAt { get; } = [];

    public DateTimeOffset? AnsweredAt { get; set; }
    public string? Status { get; set; }
    public bool Failed { get; set; }

    /// <summary>You saw how it ended: it is put away when you leave its place.</summary>
    public bool SeenDone { get; set; }
}

/// <summary>
/// The steps of a command on its way, as its progress card and the timeline's rail show them: sent from the phone,
/// picked up by the PC, each step there, then Claude has it; and what may hold it up when it is slow.
/// </summary>
internal sealed class DeliveryProgress(ISyncedCollection<WorkspaceResultRecord> results, ISyncService? sync, TimeProvider time, Func<string> hostName)
{
    /// <summary>
    /// The steps of <paramref name="d"/> so far, each with how long it took (or has taken while it runs). A step's time
    /// is from when the phone first saw it to when it saw the next one, so it is noted for every delivery, on screen or not.
    /// </summary>
    public List<ProgressStep> StepsOf(Delivery d)
    {
        var p = d.Sent;
        var host = hostName();
        var steps = new List<(string Text, ProgressState State)>();
        if (p.Kind == WorkspaceCommandKind.Reopen && d.Title is { } title) steps.Add((title, p.Done ? p.Failed ? ProgressState.Failed : ProgressState.Done : ProgressState.Active));
        var result = results.Get(p.CommandId);
        var pushed = result is not null || sync is null || sync.Status.LastSyncedAt is { } synced && synced >= p.At;
        steps.Add((pushed ? "Sent from this phone" : "Sending from this phone…", pushed ? ProgressState.Done : p.Failed ? ProgressState.Failed : ProgressState.Active));
        if (pushed)
        {
            steps.Add(result is not null
                ? ($"{host} picked it up", ProgressState.Done)
                : (p.Failed ? $"{host} did not answer" : $"Waiting for {host}…", p.Failed ? ProgressState.Failed : ProgressState.Active));
            for (var i = 0; i < d.PcSteps.Count; i++)
            {
                var last = i == d.PcSteps.Count - 1;
                steps.Add((d.PcSteps[i], last && !p.Done ? ProgressState.Active : last && p.Failed && result?.Status == WorkspaceCommandStatus.Failed ? ProgressState.Failed : ProgressState.Done));
            }
            switch (result?.Status)
            {
                case WorkspaceCommandStatus.Received:
                    steps.Add((p.Kind == WorkspaceCommandKind.Reopen ? "Open in VS Code" : "Claude received it", ProgressState.Done));
                    if (d.AwaitGaveUp) steps.Add(("It did not show up here yet. Pull the tabs again with ⟳, or look on the PC.", ProgressState.Failed));
                    else if (AwaitingTab(d)) steps.Add(("Opening it here…", ProgressState.Active));
                    break;
                case WorkspaceCommandStatus.Sent:
                    steps.Add((result.Error ?? "Typed into VS Code; Claude has not confirmed it yet", ProgressState.Done));
                    break;
                case WorkspaceCommandStatus.Failed:
                    steps.Add((result.Error ?? "It could not be sent.", ProgressState.Failed));
                    break;
            }
        }
        var now = time.GetLocalNow();
        // When each step was first seen (by its place in the list: a step's words change as it ends, its place does not).
        for (var i = d.StepAt.Count; i < steps.Count; i++) d.StepAt.Add(now);
        var list = new List<ProgressStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var start = i == 0 ? p.At : d.StepAt[i];
            var (text, state) = steps[i];
            var took = i + 1 < steps.Count ? Took(d.StepAt[i + 1] - start)
                : state == ProgressState.Active ? Took(now - start)
                // The last word: how long it all took.
                : steps.Count > 1 ? "in " + Took(d.StepAt[i] - p.At) : "";
            list.Add(new ProgressStep(text, state, took));
        }
        return list;
    }

    /// <summary>Said under the running step of <paramref name="d"/> when it takes longer than it should.</summary>
    public string HintOf(Delivery d)
    {
        if (d.Sent is not { Done: false } p) return "";
        var waited = time.GetLocalNow() - p.At;
        if (results.Get(p.CommandId) is null)
            return waited > TimeSpan.FromSeconds(12) ? $"{hostName()} has not picked it up yet. Is the PC awake, with Helm running? It keeps trying." : "";
        return waited > TimeSpan.FromSeconds(25) ? "Slower than usual: VS Code or Claude may be busy on the PC. It keeps going." : "";
    }

    /// <summary>The steps of <paramref name="d"/> as timeline rows; the running one says what holds it up when it is slow.</summary>
    public IEnumerable<TimelineRow> StepRows(Delivery d)
    {
        var steps = StepsOf(d);
        var hint = HintOf(d);
        return steps.Select((step, i) => TimelineRow.Step(step, i == steps.Count - 1 && step.IsActive ? hint : null));
    }

    /// <summary>The PC is done; the new or reopened tab has not shown up on the phone yet.</summary>
    public static bool AwaitingTab(Delivery d) => d.AwaitSession is not null && d.Sent is { Done: true, Failed: false, Kind: WorkspaceCommandKind.NewSession or WorkspaceCommandKind.Reopen } && !d.AwaitGaveUp;

    /// <summary>"0.6 s", "12 s", "1 min 05 s".</summary>
    private static string Took(TimeSpan t) => t.TotalSeconds < 10 ? $"{Math.Max(0, t.TotalSeconds):0.0} s" : Seconds(t);

    /// <summary>"12 s", "1 min 05 s".</summary>
    public static string Seconds(TimeSpan t) => t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";
}
