using Helm.Ai.Assistant;

namespace Helm.Ai;

/// <summary>
/// The records of Helm's automatic jobs (the morning brief, the evening review…, removed in 0.95) that are still on the
/// sync servers. Nothing reads or writes them any more; their collections stay registered so sync keeps treating them
/// as before (H4d decides about the data). Their shape is the old one.
/// </summary>
public static class LeftoverAiRecords
{
    /// <summary>What the PC's automatic jobs wrote (<see cref="AiBriefRecord"/>).</summary>
    public const string BriefsCollection = "ai.briefs";

    /// <summary>When each automatic job last ran (<see cref="AutoJobRunRecord"/>).</summary>
    public const string RunsCollection = "ai.autoruns";
}

/// <summary>A brief an automatic job wrote (collection <see cref="LeftoverAiRecords.BriefsCollection"/>).</summary>
public sealed record AiBriefRecord
{
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ReadAt { get; init; }

    public string? Device { get; init; }

    public IReadOnlyList<AiJobAction> Actions { get; init; } = [];

    public DateTimeOffset? UndoneAt { get; init; }
}

/// <summary>An automatic job's slot as it last ran (collection <see cref="LeftoverAiRecords.RunsCollection"/>).</summary>
public sealed record AutoJobRunRecord
{
    public required string Period { get; init; }

    public required string Device { get; init; }

    public string State { get; init; } = "";

    public int Attempts { get; init; }

    public DateTimeOffset At { get; init; }

    public string? Problem { get; init; }

    public IReadOnlyList<string> Remembered { get; init; } = [];
}
