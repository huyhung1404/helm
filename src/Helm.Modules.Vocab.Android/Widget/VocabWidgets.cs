using Android.Appwidget;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using RemoteViews = Android.Widget.RemoteViews;
using AndroidUri = Android.Net.Uri;
using ViewStates = Android.Views.ViewStates;

namespace Helm.Modules.Vocab;

/// <summary>One line of the widget's answer list: what it says and how it looks.</summary>
internal enum VocabWidgetLineKind
{
    Word,
    Reading,
    Meaning,
    Part,
    Example,
}

internal sealed record VocabWidgetLine(string Text, VocabWidgetLineKind Kind);

/// <summary>
/// Draws the Vocabulary widgets and handles their taps. Each widget remembers its word and whether the meaning is
/// shown; the answer is a list that scrolls when it is long, with Forgot and Remembered as small buttons on the top
/// row. Colours follow the widget settings (background, opacity, text), as on Tracker and Wallet. Runs in the app
/// process, possibly without the activity (Android starts the provider on its own), so services come from
/// <see cref="HelmAndroidServices"/>.
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
        var (enabled, store, look) = Load();
        var colours = Colours.For(context, look);
        var word = store is null || WordId(context, widgetId) is not { } id ? null : store.GetWord(id);
        if (store is not null && enabled && word is not { IsAnalyzed: true })
        {
            // Its word was deleted, or none was picked yet: pick one now.
            var next = HelmAndroidServices.Current.GetRequiredService<VocabQuiz>().Next();
            Save(context, widgetId, next?.Id, revealed: false);
            word = next?.Value;
        }
        var revealed = word is not null && IsRevealed(context, widgetId);

        var bg = R.Id(context, "vocab_widget_bg");
        views.SetInt(bg, "setColorFilter", look.BackgroundColor is { } solid ? unchecked((int)(solid | 0xFF000000)) : 0);
        views.SetInt(bg, "setImageAlpha", look.Alpha);

        var hasWord = enabled && word is not null;
        views.SetViewVisibility(R.Id(context, "vocab_widget_card"), hasWord ? ViewStates.Visible : ViewStates.Gone);
        views.SetViewVisibility(R.Id(context, "vocab_widget_empty"), hasWord ? ViewStates.Gone : ViewStates.Visible);
        views.SetTextViewText(R.Id(context, "vocab_widget_empty"), !enabled
            ? "Vocabulary is turned off. Turn it on in Helm."
            : store is null ? "Open Helm to load your words."
            : "Add words in Helm. Once an AI agent breaks them down, they show up here to guess.");
        views.SetTextColor(R.Id(context, "vocab_widget_empty"), colours.Secondary);
        views.SetTextColor(R.Id(context, "vocab_widget_title"), colours.Secondary);
        views.SetTextColor(R.Id(context, "vocab_widget_word"), colours.Main);
        views.SetTextColor(R.Id(context, "vocab_widget_hint"), colours.Secondary);
        views.SetInt(R.Id(context, "vocab_widget_skip"), "setColorFilter", colours.Secondary.ToArgb());
        views.SetInt(R.Id(context, "vocab_widget_forgot"), "setColorFilter", colours.Main.ToArgb());
        if (hasWord)
        {
            views.SetTextViewText(R.Id(context, "vocab_widget_word"), word!.Text);
            views.SetViewVisibility(R.Id(context, "vocab_widget_word_area"), revealed ? ViewStates.Gone : ViewStates.Visible);
            views.SetViewVisibility(R.Id(context, "vocab_widget_list"), revealed ? ViewStates.Visible : ViewStates.Gone);
            views.SetViewVisibility(R.Id(context, "vocab_widget_forgot"), revealed ? ViewStates.Visible : ViewStates.Gone);
            views.SetViewVisibility(R.Id(context, "vocab_widget_remembered"), revealed ? ViewStates.Visible : ViewStates.Gone);
            if (revealed) SetList(context, views, widgetId, Lines(store!, word), colours);
        }

        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_word_area"), Broadcast(context, widgetId, ActionReveal, 0));
        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_remembered"), Broadcast(context, widgetId, ActionRemembered, 1));
        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_forgot"), Broadcast(context, widgetId, ActionForgot, 2));
        views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_skip"), Broadcast(context, widgetId, ActionSkip, 3));
        // The title, and a tap on the answer, open the word in Helm with its whole tree.
        var wordId = revealed ? WordId(context, widgetId) : null;
        if (OpenAppIntent(context, RequestCode(widgetId, 4), wordId) is { } open)
        {
            views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_title"), open);
            views.SetOnClickPendingIntent(R.Id(context, "vocab_widget_empty"), open);
        }
        if (revealed && OpenAppIntent(context, RequestCode(widgetId, 5), wordId, mutable: true) is { } openWord)
            views.SetPendingIntentTemplate(R.Id(context, "vocab_widget_list"), openWord);
        manager.UpdateAppWidget(widgetId, views);
        if (revealed && !OperatingSystem.IsAndroidVersionAtLeast(31))
            manager.NotifyAppWidgetViewDataChanged(widgetId, R.Id(context, "vocab_widget_list"));
    }

    /// <summary>The answer, line by line: the word, its reading, its meaning, each part with its meaning, an example.</summary>
    internal static IReadOnlyList<VocabWidgetLine> Lines(VocabStore store, VocabWord word)
    {
        var lines = new List<VocabWidgetLine> { new(word.Text, VocabWidgetLineKind.Word) };
        var reading = string.Join("  ", new[] { word.Phonetic, word.HanViet, word.Traditional }.Where(s => s.Length > 0));
        if (reading.Length > 0) lines.Add(new(reading, VocabWidgetLineKind.Reading));
        lines.Add(new(word.Meaning, VocabWidgetLineKind.Meaning));
        lines.AddRange(VocabTree.Lines(store, word).Select(l => new VocabWidgetLine(l, VocabWidgetLineKind.Part)));
        if (word.Examples.FirstOrDefault() is { } example)
        {
            lines.Add(new(example.Sentence, VocabWidgetLineKind.Example));
            if (example.Translation.Length > 0) lines.Add(new(example.Translation, VocabWidgetLineKind.Example));
        }
        return lines;
    }

    /// <summary>The lines of a widget whose meaning is shown (the list service asks for them before Android 12).</summary>
    internal static (IReadOnlyList<VocabWidgetLine> Lines, Colours Colours) LinesOf(Context context, int widgetId)
    {
        var (_, store, look) = Load();
        var colours = Colours.For(context, look);
        if (store is null || WordId(context, widgetId) is not { } id || store.GetWord(id) is not { IsAnalyzed: true } word) return ([], colours);
        return (Lines(store, word), colours);
    }

    internal static RemoteViews BuildLine(Context context, VocabWidgetLine line, Colours colours)
    {
        var row = new RemoteViews(context.PackageName, R.Layout(context, "vocab_widget_line"));
        var view = R.Id(context, "vocab_widget_line");
        var (size, colour, bold) = line.Kind switch
        {
            VocabWidgetLineKind.Word => (22, colours.Main, true),
            VocabWidgetLineKind.Reading => (13, colours.Secondary, false),
            VocabWidgetLineKind.Meaning => (15, colours.Main, false),
            VocabWidgetLineKind.Part => (12, colours.Accent, false),
            _ => (12, colours.Secondary, false),
        };
        if (bold)
        {
            var text = new SpannableString(line.Text);
            text.SetSpan(new StyleSpan(TypefaceStyle.Bold), 0, line.Text.Length, SpanTypes.ExclusiveExclusive);
            row.SetTextViewText(view, text);
        }
        else row.SetTextViewText(view, line.Text);
        row.SetTextViewTextSize(view, (int)Android.Util.ComplexUnitType.Sp, size);
        row.SetTextColor(view, colour);
        // Any line opens the word in Helm (the list's template carries the word).
        row.SetOnClickFillInIntent(view, new Intent());
        return row;
    }

    private static void SetList(Context context, RemoteViews views, int widgetId, IReadOnlyList<VocabWidgetLine> lines, Colours colours)
    {
        var list = R.Id(context, "vocab_widget_list");
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            // Android 12+: the lines travel with the widget itself (no service).
            var items = new RemoteViews.RemoteCollectionItems.Builder().SetHasStableIds(false)!.SetViewTypeCount(1)!;
            for (var i = 0; i < lines.Count; i++) items.AddItem(i, BuildLine(context, lines[i], colours));
            views.SetRemoteAdapter(list, items.Build()!);
            return;
        }
        // Lines come from VocabWidgetService; the data URI keeps one factory per widget.
        var adapter = new Intent(context, typeof(VocabWidgetService));
        adapter.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        adapter.SetData(AndroidUri.Parse(adapter.ToUri(IntentUriType.Scheme)));
        views.SetRemoteAdapter(list, adapter);
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

    private static (bool Enabled, VocabStore? Store, WidgetLook Look) Load()
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var settings = services.GetRequiredService<ISettingsStoreFactory>();
            var general = settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            var enabled = !general.EnabledModules.TryGetValue(VocabIds.ModuleId, out var on) || on;
            return (enabled, services.GetRequiredService<VocabStore>(), settings.Get<VocabSettings>(VocabIds.ModuleId).Current.WidgetLook);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not load the Vocabulary widget data");
            return (true, null, new WidgetLook(WidgetBackground.Card, 100, WidgetText.Automatic));
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

    /// <param name="mutable">A list's click template: each line fills in its own (empty) extras.</param>
    private static PendingIntent? OpenAppIntent(Context context, int requestCode, string? wordId = null, bool mutable = false)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, VocabIds.ModuleId);
        // The page opens this word with its whole tree (ShellIntents hands it over).
        if (wordId is not null) launch.PutExtra(ShellIntents.ExtraItem, wordId);
        var flags = !mutable ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable
            : PendingIntentFlags.UpdateCurrent;
        return PendingIntent.GetActivity(context, requestCode, launch, flags);
    }

    private static int RequestCode(int widgetId, int slot) => widgetId * 8 + slot;

    /// <summary>The widget's colours on its background: the settings', or the phone theme's when they follow it.</summary>
    internal sealed record Colours(Color Main, Color Secondary, Color Accent)
    {
        public static Colours For(Context context, WidgetLook look)
        {
            var night = ((context.Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;
            var dark = look.LightText ?? night;
            var (main, secondary) = look.TextColors ?? (night ? (0xFFFFFFFFu, 0xFFC5C5C5u) : (0xFF1A1A1Au, 0xFF5F5F5Fu));
            // On the accent background the accent would vanish: the parts take the main colour there.
            var accent = look.Background == WidgetBackground.Accent ? main : dark ? 0xFF4CC2FFu : 0xFF0067C0u;
            return new Colours(Of(main), Of(secondary), Of(accent));
        }

        private static Color Of(uint argb) => new(unchecked((int)argb));
    }

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
