namespace Helm.Modules.Vocab;

/// <summary>Short texts about how well a word is known.</summary>
public static class VocabFormat
{
    /// <summary>"New", "Level 3/8 · due now", "Level 3/8 · next in 4 days".</summary>
    public static string Level(VocabWordStats stats, DateTimeOffset now)
    {
        if (stats.IsNew) return "New";
        var level = $"Level {stats.Level}/{VocabQuiz.TopLevel}";
        return stats.Due is not { } due || due <= now ? $"{level} · due now" : $"{level} · next {In(due - now)}";
    }

    /// <summary>"in 10 minutes", "in 3 hours", "tomorrow", "in 4 days".</summary>
    public static string In(TimeSpan gap) => gap switch
    {
        { TotalMinutes: < 60 } => $"in {Math.Max(1, (int)Math.Ceiling(gap.TotalMinutes))} minutes",
        { TotalHours: < 24 } => $"in {(int)Math.Ceiling(gap.TotalHours)} hours",
        { TotalDays: < 2 } => "tomorrow",
        _ => $"in {(int)Math.Round(gap.TotalDays)} days",
    };
}
