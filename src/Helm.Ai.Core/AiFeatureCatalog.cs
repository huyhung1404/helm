using System.Reflection;

namespace Helm.Ai;

/// <summary>One AI feature as the settings list it.</summary>
/// <param name="Automatic">It runs by itself (not started by the user): on a PC agent only until the user says otherwise.</param>
/// <param name="OnByDefault">On until the user turns it off; false: off until they turn it on (a paid extra).</param>
/// <param name="Route">Where it runs until the user says otherwise; null: as the default choice does.</param>
/// <param name="Tool">The tool it belongs to (a module id): the settings show that tool's icon while it is in the app.</param>
/// <param name="Symbol">
/// Its Fluent symbol without a size ("Mic"), drawn when it belongs to no tool or the tool is not there; the PC adds "24".
/// </param>
public sealed record AiFeatureInfo(string Id, string Name, string Description, bool Automatic, bool OnByDefault = true,
    AiRoutePolicy? Route = null, string? Tool = null, string Symbol = AiFeatureInfo.DefaultSymbol)
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
        new(AiFeatureIds.Default, "Default", "The model a feature uses until you pick one for it", false),
        new(AiFeatureIds.TextActions, "Translate / rewrite selection", "Selected text in any app, from the phone's text menu", false, Symbol: "Translate"),
        new(AiFeatureIds.VocabQuick, "Word analysis", "Roots and meaning for a word added on the phone", false, Tool: "vocab", Symbol: "BookLetter"),
        new(AiFeatureIds.SentenceExplain, "Explain sentence", "Novel Reader: hold a sentence", false, Tool: "novel-reader", Symbol: "BookOpen"),
        new(AiFeatureIds.TaskProposals, "Task proposals", "Tracker: steps for a big task, tasks from a note", false, Tool: "tracker", Symbol: "TaskListAdd"),
        new(AiFeatureIds.WalletReadNotification, "Read a bank notification", "Wallet: one Helm could not read, when you ask", false, Tool: "wallet", Symbol: "Wallet"),
        new(AiFeatureIds.WalletCategorize, "Auto-classify transactions", "Wallet: files a new payment the history cannot", true, Tool: "wallet", Symbol: "Wallet"),
    ];

    public static IReadOnlyList<AiFeatureInfo> All { get; } = Build();

    public static AiFeatureInfo? Find(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// The choice a feature runs with now: its own, else the default model, on,
    /// and on a PC only when it runs by itself (as <see cref="Assistant.AiTextService"/> does).
    /// </summary>
    public static AiFeatureChoice Effective(AiFeatureSettings settings, string id)
    {
        if (settings.Features.TryGetValue(id, out var own)) return own;
        if (id == AiFeatureIds.Default) return settings.For(id);
        var inherited = settings.For(AiFeatureIds.Default).Copy();
        var info = Find(id);
        inherited.Enabled = info?.OnByDefault ?? true;
        inherited.At = null;
        if (info?.Automatic == true) inherited.Route = AiRoutePolicy.PcOnly;
        if (info?.Route is { } route) inherited.Route = route;
        return inherited;
    }

    private static List<AiFeatureInfo> Build()
    {
        var all = Known.ToList();
        var ids = typeof(AiFeatureIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
        foreach (var id in ids.Where(id => all.All(f => f.Id != id)))
            all.Add(new AiFeatureInfo(id, Name(id), "", false));
        return all;
    }

    /// <summary>"scratch-tidy" → "Scratch tidy".</summary>
    private static string Name(string id)
    {
        var words = id.Replace('-', ' ');
        return words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
