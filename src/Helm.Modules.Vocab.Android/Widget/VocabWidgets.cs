using Android.Appwidget;
using Android.Content;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using RemoteViews = Android.Widget.RemoteViews;
using ViewStates = Android.Views.ViewStates;

namespace Helm.Modules.Vocab;

/// <summary>
/// Draws the Vocabulary widgets and handles their taps. Each widget remembers its word and whether the meaning is
/// shown. Runs in the app process, possibly without the activity (Android starts the provider on its own), so
/// services come from <see cref="HelmAndroidServices"/>.
/// </summary>
internal static class VocabWidgets
{
    public const string ActionReveal = "com.huyhung1404.helm.vocab.WIDGET_REVEAL";
    public const string ActionRemembered = "com.huyhung1404.helm.vocab.WIDGET_REMEMBERED";
    public const string ActionForgot = "com.huyhung1404.helm.vocab.WIDGET_FORGOT";
    public const string ActionSkip = "com.huyhung1404.helm.vocab.WIDGET_SKIP";

    private const string PreferencesName = "helm_vocab_widgets";

    public static void RefreshAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        foreach (var id in Ids(context, manager)) Update(context, manager, id);
    }

    /// <summary>The phone was unlocked: every widget gets a new word, meaning hidden.</summary>
    public static void NextWordAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        foreach (var id in Ids(context, manager)) NextWord(context, id, redraw: false);
        RefreshAll(context);
    }

    /// <summary>A new word unless the meaning is shown and waits for an answer.</summary>
    public static void NextWordIfIdle(Context context, int widgetId)
    {
        if (!IsRevealed(context, widgetId)) NextWord(context, widgetId, redraw: false);
    }

    public static void NextWord(Context context, int widgetId) => NextWord(context, widgetId, redraw: true);

    private static void NextWord(Context context, int widgetId, bool redraw)
    {
        try
        {
            var next = HelmAndroidServices.Current.GetRequiredService<VocabQuiz>().Next(except: WordId(context, widgetId));
            Save(context, widgetId, next?.Id, revealed: false);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not pick a word for the Vocabulary widget");
        }
        if (redraw) RefreshAll(context);
    }

    public static void Reveal(Context context, int widgetId)
    {
        Save(context, widgetId, WordId(context, widgetId), revealed: true);
        RefreshAll(context);
    }

    /// <summary>Records the answer (synced like one given in the app), then the next word.</summary>
    public static void Answer(Context context, int widgetId, bool remembered)
    {
        if (WordId(context, widgetId) is { } wordId && IsRevealed(context, widgetId))
        {
            try
            {
                HelmAndroidServices.Current.GetRequiredService<VocabStore>().Record(wordId, remembered);
            }
            catch (Exception ex)
            {
                Log(ex, "Could not record a Vocabulary answer from the widget");
            }
        }
        NextWord(context, widgetId);
    }

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        var views = new RemoteViews(context.PackageName, R.Layout(context, "vocab_widget"));
        var (enabled, store) = Load();
        var word = store is null || WordId(context, widgetId) is not { } id ? null : store.GetWord(id);
        if (store is not null && enabled && word is not { IsAnalyzed: true })
        {
            // Its word was deleted, or none was picked yet: pick one now.
            var next = HelmAndroidServices.Current.GetRequiredService<VocabQuiz>().Next();
            Save(context, widgetId, next?.Id, revealed: false);
            word = next?.Value;
        }
        var revealed = word is not null && IsRevealed(context, widgetId);

        var hasWord = enabled && word is not null;
        views.SetViewVisibility(R.Id(context, "vocab_widget_card"), hasWord ? ViewStates.Visible : ViewStates.Gone);
        views.SetViewVisibility(R.Id(context, "vocab_widget_empty"), hasWord ? ViewStates.Gone : ViewStates.Visible);
        views.SetTextViewText(R.Id(context, "vocab_widget_empty"), !enabled
            ? "Vocabulary is turned off. Turn it on in Helm."
            : store is null ? "Open Helm to load your words."
            : "Add words in Helm. Once an AI agent breaks them down, they show up here to guess.");
        if (hasWord)
        {
            views.SetTextViewText(R.Id(context, "vocab_widget_word"), word!.Text);
            // With the meaning shown there is a lot to fit: the title row goes and the word gets smaller.
            views.SetViewVisibility(R.Id(context, "vocab_widget_header"), revealed ? ViewStates.Gone : ViewStates.Visible);
            views.SetTextViewTextSize(R.Id(context, "vocab_widget_word"), (int)Android.Util.ComplexUnitType.Sp, revealed ? 22 : 28);
            views.SetViewVisibility(R.Id(context, "vocab_widget_hint"), revealed ? ViewStates.Gone : ViewStates.Visible);
            views.SetViewVisibility(R.Id(context, "vocab_widget_answer"), revealed ? ViewStates.Visible : ViewStates.Gone);
            var reading = string.Join("  ", new[] { word.Phonetic, word.HanViet, word.Traditional }.Where(s => s.Length > 0));
            views.SetTextViewText(R.Id(context, "vocab_widget_phonetic"), reading);
            views.SetTextViewText(R.Id(context, "vocab_widget_meaning"), word.Meaning);
            // Each part with its meaning: enough to remember a forgotten word at once.
            var parts = string.Join(Environment.NewLine, VocabTree.Lines(store!, word));
            views.SetTextViewText(R.Id(context, "vocab_widget_parts"), parts);
            views.SetViewVisibility(R.Id(context, "vocab_widget_parts"), parts.Length > 0 ? ViewStates.Visible : ViewStates.Gone);
        }

        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_word_area"), Broadcast(context, widgetId, ActionReveal, 0));
        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_remembered"), Broadcast(context, widgetId, ActionRemembered, 1));
        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_forgot"), Broadcast(context, widgetId, ActionForgot, 2));
        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_skip"), Broadcast(context, widgetId, ActionSkip, 3));
        // The parts open the word in Helm with its whole tree.
        if (OpenAppIntent(context, RequestCode(widgetId, 5), WordId(context, widgetId)) is { } openWord)
            views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_parts"), openWord);
        if (OpenAppIntent(context, RequestCode(widgetId, 4)) is { } open)
        {
            views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_title"), open);
            views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_empty"), open);
        }
        manager.UpdateAppWidget(widgetId, views);
    }

    /// <summary>Whether the launcher can place a widget when the app asks (Android 8+, most launchers).</summary>
    public static bool CanRequestPin(Context context)
    {
        try { return AppWidgetManager.GetInstance(context)?.IsRequestPinAppWidgetSupported == true; }
        catch (Exception) { return false; }
    }

    /// <summary>Asks the launcher to add the widget; the launcher shows its own confirmation.</summary>
    public static void RequestPin(Context context)
    {
        try
        {
            var provider = new ComponentName(context, Java.Lang.Class.FromType(typeof(VocabWidgetProvider)));
            AppWidgetManager.GetInstance(context)?.RequestPinAppWidget(provider, null, null);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not ask the launcher to add the Vocabulary widget");
        }
    }

    public static void Forget(Context context, int widgetId) =>
        Preferences(context).Edit()?.Remove(WordKey(widgetId))?.Remove(RevealedKey(widgetId))?.Apply();

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static int[] Ids(Context context, AppWidgetManager manager) =>
        manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(VocabWidgetProvider)))) ?? [];

    private static (bool Enabled, VocabStore? Store) Load()
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var general = services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            var enabled = !general.EnabledModules.TryGetValue(VocabIds.ModuleId, out var on) || on;
            return (enabled, services.GetRequiredService<VocabStore>());
        }
        catch (Exception ex)
        {
            Log(ex, "Could not load the Vocabulary widget data");
            return (true, null);
        }
    }

    private static string? WordId(Context context, int widgetId) => Preferences(context).GetString(WordKey(widgetId), null);

    private static bool IsRevealed(Context context, int widgetId) => Preferences(context).GetBoolean(RevealedKey(widgetId), false);

    private static void Save(Context context, int widgetId, string? wordId, bool revealed) =>
        Preferences(context).Edit()?.PutString(WordKey(widgetId), wordId)?.PutBoolean(RevealedKey(widgetId), revealed)?.Apply();

    private static string WordKey(int widgetId) => $"word_{widgetId}";

    private static string RevealedKey(int widgetId) => $"revealed_{widgetId}";

    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;

    private static PendingIntent Broadcast(Context context, int widgetId, string action, int slot)
    {
        var intent = new Intent(context, typeof(VocabWidgetProvider)).SetAction(action)!;
        intent.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        return PendingIntent.GetBroadcast(context, RequestCode(widgetId, slot), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static PendingIntent? OpenAppIntent(Context context, int requestCode, string? wordId = null)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, VocabIds.ModuleId);
        // The page opens this word with its whole tree (ShellIntents hands it over).
        if (wordId is not null) launch.PutExtra(ShellIntents.ExtraItem, wordId);
        return PendingIntent.GetActivity(context, requestCode, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static int RequestCode(int widgetId, int slot) => widgetId * 8 + slot;

    private static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Vocab.Widget").LogWarning(ex, message);
        }
        catch (Exception)
        {
            Android.Util.Log.Warn("Helm", $"{message}: {ex}");
        }
    }
}

/// <summary>
/// Resource ids by name. The widget's layouts live in this library, and looking them up by name at run time does not
/// depend on how the app's resource designer maps library ids.
/// </summary>
internal static class R
{
    private static readonly Dictionary<string, int> s_cache = new(StringComparer.Ordinal);

    public static int Layout(Context context, string name) => Get(context, name, "layout");

    public static int Id(Context context, string name) => Get(context, name, "id");

    public static int Get(Context context, string name, string type)
    {
        var key = type + "/" + name;
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var id)) return id;
            id = context.Resources?.GetIdentifier(name, type, context.PackageName) ?? 0;
            if (id == 0) throw new InvalidOperationException($"Missing Android resource {key}.");
            s_cache[key] = id;
            return id;
        }
    }
}
