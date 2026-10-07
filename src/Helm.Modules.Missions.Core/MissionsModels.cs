using System.Text.Json.Serialization;

namespace Helm.Modules.Missions;

public enum MissionStatus
{
    /// <summary>Created (imported or by an AI agent) but not started: the pace counts from Start.</summary>
    Planned,

    Active,

    /// <summary>On hold: paused time is left out of the pace.</summary>
    Paused,

    Completed,

    /// <summary>Given up; can be restored.</summary>
    Abandoned,
}

/// <summary>Where a mission came from.</summary>
public enum MissionSource
{
    Import,
    /// <summary>Made by an AI agent over MCP (any client; the name is kept because it is stored and synced).</summary>
    Claude,
    Manual,
}

/// <summary>A named group of steps inside a mission, in order, with its own reward.</summary>
public sealed record MissionPhase
{
    /// <summary>Short random id; steps point to their phase with it.</summary>
    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    public string Reward { get; init; } = "";

    /// <summary>When the user marked the phase's reward as taken.</summary>
    public DateTimeOffset? RewardClaimedAt { get; init; }
}

/// <summary>A goal reached through ordered steps (synced record in <c>missions.missions</c>).</summary>
public sealed record Mission
{
    public string Title { get; init; } = "";

    /// <summary>The measurable end result.</summary>
    public string Goal { get; init; } = "";

    /// <summary>The AI's caveat from the import (e.g. "the deadline is tight").</summary>
    public string Note { get; init; } = "";

    /// <summary>The user's reward for the whole mission.</summary>
    public string Reward { get; init; } = "";

    public DateOnly? Deadline { get; init; }

    /// <summary>The phases in order.</summary>
    public IReadOnlyList<MissionPhase> Phases { get; init; } = [];

    public MissionStatus Status { get; init; }

    public MissionSource Source { get; init; }

    /// <summary>Position in the mission picker (ascending).</summary>
    public double Order { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the user pressed Start mission; the pace counts from here.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Since when the mission is paused or abandoned (null while it runs).</summary>
    public DateTimeOffset? PausedAt { get; init; }

    /// <summary>Paused time already over (left out of the pace).</summary>
    public TimeSpan PausedTotal { get; init; }

    public DateTimeOffset? RewardClaimedAt { get; init; }

    public MissionPhase? Phase(string key) => Phases.FirstOrDefault(p => p.Key == key);

    public int PhaseIndex(string key)
    {
        for (var i = 0; i < Phases.Count; i++)
            if (Phases[i].Key == key) return i;
        return -1;
    }
}

/// <summary>
/// What a step needs to study or where to practise, in any field: a link, a list to learn, the theory with examples,
/// where the test is. Nothing in it is tied to a subject: the label and the table's columns are whatever the plan
/// names them ("Vocabulary" with Word / Pinyin / Meaning, "Formulas" with Formula / When to use). Every field is
/// optional; a resource has at least a title, a link, a text or a row.
/// </summary>
public sealed record MissionResource
{
    /// <summary>A short free label shown as a chip ("Vocabulary", "Grammar", "Mock test"); empty for a plain line.</summary>
    public string Label { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>An http or https address.</summary>
    public string Url { get; init; } = "";

    /// <summary>The explanation: the theory, how to get to the test, tips.</summary>
    public string Text { get; init; } = "";

    /// <summary>The table's column names, in order; empty when the rows have one unnamed column.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The table: one list of cells per row, in the order of <see cref="Columns"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];
}

/// <summary>A tick inside a step.</summary>
public sealed record ChecklistItem
{
    public string Text { get; init; } = "";

    public DateTimeOffset? DoneAt { get; init; }

    [JsonIgnore]
    public bool IsDone => DoneAt is not null;
}

/// <summary>
/// One step of a mission (synced record in <c>missions.steps</c>). One record per step, so ticking a step on the phone
/// and editing another on the PC never collide.
/// </summary>
public sealed record MissionStep
{
    public string MissionId { get; init; } = "";

    public string PhaseKey { get; init; } = "";

    /// <summary>Position in the whole mission (ascending; a phase's steps are contiguous).</summary>
    public double Order { get; init; }

    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>How the user checks that the step is done.</summary>
    public string DoneWhen { get; init; } = "";

    public double EstimateDays { get; init; } = 1;

    /// <summary>
    /// The resources as one line each ("title — url", a book title): all Helm 0.25 and older read. Written next to
    /// <see cref="Materials"/>, so a step stays readable there.
    /// </summary>
    public IReadOnlyList<string> Resources { get; init; } = [];

    /// <summary>
    /// The resources with their details (a word list, a grammar rule, where the test is), when any has more than a line
    /// holds. Older Helm versions do not know it and drop it when they edit the step; <see cref="Resources"/> is then used.
    /// Read both through <see cref="MissionResources.Of"/>.
    /// </summary>
    public IReadOnlyList<MissionResource> Materials { get; init; } = [];

    public IReadOnlyList<ChecklistItem> Checklist { get; init; } = [];

    /// <summary>Set by Start, or on completion from the previous step's finish when Start was never pressed.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public bool StartedExplicitly { get; init; }

    /// <summary>Done or skipped.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    public bool Skipped { get; init; }

    /// <summary>Written when completing (what was done, a result, a link).</summary>
    public string Note { get; init; } = "";

    /// <summary>
    /// The task this step was sent to in another tool (Tracker), through <c>ITaskBridge</c>; finishing either one
    /// finishes the other. Older Helm versions do not know it and may drop it when they edit the step.
    /// </summary>
    public string? TaskId { get; init; }

    [JsonIgnore]
    public bool IsDone => CompletedAt is not null;
}

public enum MissionEventKind
{
    MissionCreated,
    MissionStarted,
    StepStarted,
    ChecklistTicked,
    StepCompleted,
    StepSkipped,
    StepReopened,
    PhaseCompleted,
    MissionCompleted,
    Paused,
    Resumed,
    Abandoned,
    RewardClaimed,
}

/// <summary>
/// One line of the append-only history (<c>missions.history</c>), with the titles at the time, so the timeline survives
/// edits and deletions.
/// </summary>
public sealed record MissionEvent
{
    public MissionEventKind Kind { get; init; }

    public DateTimeOffset At { get; init; }

    public string MissionId { get; init; } = "";

    public string MissionTitle { get; init; } = "";

    public string? StepId { get; init; }

    public string StepTitle { get; init; } = "";

    public string? PhaseKey { get; init; }

    public string Note { get; init; } = "";
}

/// <summary>A mission to create: from the import, an AI agent, or the preview after edits.</summary>
public sealed record MissionDraft(
    string Title,
    string Goal,
    string Note,
    string Reward,
    DateOnly? Deadline,
    IReadOnlyList<PhaseDraft> Phases)
{
    public int StepCount => Phases.Sum(p => p.Steps.Count);

    public double EstimateDays => Phases.Sum(p => p.Steps.Sum(s => s.EstimateDays));
}

public sealed record PhaseDraft(string Title, string Reward, IReadOnlyList<StepDraft> Steps);

public sealed record StepDraft(
    string Title,
    string Description = "",
    string DoneWhen = "",
    double EstimateDays = 1,
    IReadOnlyList<string>? Checklist = null,
    IReadOnlyList<MissionResource>? Resources = null);

/// <summary>What completing or skipping a step led to, so the page knows which celebration to play.</summary>
public sealed record StepOutcome(
    string StepId,
    string StepTitle,
    int Done,
    int Total,
    MissionPhase? PhaseCompleted,
    bool MissionCompleted,
    string? NextStepTitle);
