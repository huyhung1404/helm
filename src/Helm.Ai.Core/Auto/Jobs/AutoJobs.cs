namespace Helm.Ai.Auto.Jobs;

/// <summary>Every automatic job, in <see cref="AutoJobCatalog"/>'s order.</summary>
public static class AutoJobs
{
    public static IReadOnlyList<IAutoJob> All() =>
    [
        new MorningBriefJob(),
        new EveningReviewJob(),
        new HabitCoachJob(),
        new SpendingDigestJob(),
        new ImportantDaysJob(),
        new NoteTitlesJob(),
        new ScratchTidyJob(),
    ];
}
