using System.Reflection;
using Helm.Ai.Auto;

namespace Helm.Ai;

/// <summary>One AI feature as the settings list it.</summary>
/// <param name="Automatic">It runs by itself (not started by the user): on a PC agent only until the user says otherwise.</param>
/// <param name="Tool">The tool it belongs to (a module id): the settings show that tool's icon while it is in the app.</param>
/// <param name="Symbol">
/// Its Fluent symbol without a size ("Mic"), drawn when it belongs to no tool or the tool is not there; the PC adds "24".
/// </param>
public sealed record AiFeatureInfo(string Id, string Name, string Description, bool Automatic, string? Tool = null, string Symbol = AiFeatureInfo.DefaultSymbol)
{
    public const string DefaultSymbol = "Sparkle";
}

/// <summary>
/// Every <see cref="AiFeatureIds"/> feature with a name the user knows: the list AI → Features (PC) and Settings › AI
/// features (phone) show. A feature id added without an entry here still shows, named after its id.
/// </summary>
public static class AiFeatureCatalog
{
    private static readonly AiFeatureInfo[] Known =
    [
        new(AiFeatureIds.AskHelm, "Ask Helm", "Voice and typed requests from the card", false, Symbol: "Mic"),
        new(AiFeatureIds.TextActions, "Translate / rewrite selection", "Selected text in any app, from the phone's text menu", false, Symbol: "Translate"),
        new(AiFeatureIds.VocabQuick, "Word analysis", "Roots and meaning for a word added on the phone", false, "vocab", "BookLetter"),
        new(AiFeatureIds.SentenceExplain, "Explain sentence", "Novel Reader: hold a sentence", false, "novel-reader", "BookOpen"),
        new(AiFeatureIds.TaskProposals, "Task proposals", "Tracker: steps for a big task, tasks from a note", false, "tracker", "TaskListAdd"),
        new(AiFeatureIds.WalletReadNotification, "Read a bank notification", "Wallet: one Helm could not read, when you ask", false, "wallet", "Wallet"),
        new(AiFeatureIds.WalletCategorize, "Auto-classify transactions", "Wallet: files a new payment the history cannot", true, "wallet", "Wallet"),
        new(AiFeatureIds.MorningBrief, "Morning brief", "Today's tasks, habits to do, yesterday's spending", true, Symbol: "WeatherSunny"),
        new(AiFeatureIds.EveningReview, "Evening review", "What got done, what slips to tomorrow", true, Symbol: "WeatherMoon"),
        new(AiFeatureIds.SpendingDigest, "Weekly spending", "Where the week's and the month's money went", true, "wallet", "Wallet"),
        new(AiFeatureIds.HabitCoach, "Habit coach", "Reads skip reasons, suggests a smaller step", true, "habits", "ArrowRepeatAll"),
        new(AiFeatureIds.ImportantDays, "Important dates", "Birthdays, giỗ and renewals, a few days before", true, Symbol: "CalendarStar"),
        new(AiFeatureIds.NoteTitles, "Note titles", "A title and tags for new notes without one", true, "notes", "Note"),
        new(AiFeatureIds.ScratchTidy, "Tidy Scratch", "What to keep or drop in Scratch (suggestions only)", true, "scratch", "Notepad"),
    ];

    public static IReadOnlyList<AiFeatureInfo> All { get; } = Build();

    public static AiFeatureInfo? Find(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// The choice a feature runs with now: its own, else its default (an automatic job's), else Ask Helm's model, on,
    /// and on a PC only when it runs by itself (as <see cref="Assistant.AiOneShot"/> and <see cref="Assistant.AiTextService"/> do).
    /// </summary>
    public static AiFeatureChoice Effective(AiFeatureSettings settings, string id)
    {
        if (settings.Features.TryGetValue(id, out var own)) return own;
        if (id == AiFeatureIds.AskHelm || AutoJobCatalog.Find(id) is not null) return settings.For(id);
        var inherited = settings.For(AiFeatureIds.AskHelm).Copy();
        inherited.Enabled = true;
        inherited.At = null;
        if (Find(id)?.Automatic == true) inherited.Route = AiRoutePolicy.PcOnly;
        return inherited;
    }

    private static List<AiFeatureInfo> Build()
    {
        var all = Known.ToList();
        var ids = typeof(AiFeatureIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
        foreach (var id in ids.Where(id => all.All(f => f.Id != id)))
            all.Add(new AiFeatureInfo(id, Name(id), "", AutoJobCatalog.Find(id) is not null));
        return all;
    }

    /// <summary>"scratch-tidy" → "Scratch tidy".</summary>
    private static string Name(string id)
    {
        var words = id.Replace('-', ' ');
        return words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
