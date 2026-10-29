using Android.Appwidget;
using Android.Content;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using MetaDataAttribute = Android.App.MetaDataAttribute;

namespace Helm.Modules.Vocab;

/// <summary>
/// The "Vocabulary" home-screen widget: one word to guess. A tap on it shows the meaning with Forgot and Remembered;
/// the answer brings the next word. A new word also comes with each unlock and every half hour.
/// </summary>
[BroadcastReceiver(Label = "Vocabulary", Exported = false)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/vocab_widget_info")]
public sealed class VocabWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        // The periodic update (and a newly placed widget): a fresh word unless one is waiting for its answer.
        foreach (var id in appWidgetIds) VocabWidgets.NextWordIfIdle(context, id);
        foreach (var id in appWidgetIds) VocabWidgets.Update(context, appWidgetManager, id);
    }

    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        if (context is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) VocabWidgets.Forget(context, id);
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        base.OnReceive(context, intent);
        if (context is null || intent is null) return;
        var id = intent.GetIntExtra(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId);
        if (id == AppWidgetManager.InvalidAppwidgetId) return;
        switch (intent.Action)
        {
            case VocabWidgets.ActionReveal:
                VocabWidgets.Reveal(context, id);
                break;
            case VocabWidgets.ActionRemembered:
                VocabWidgets.Answer(context, id, remembered: true);
                break;
            case VocabWidgets.ActionForgot:
                VocabWidgets.Answer(context, id, remembered: false);
                break;
            case VocabWidgets.ActionSkip:
                VocabWidgets.NextWord(context, id);
                break;
        }
    }
}
