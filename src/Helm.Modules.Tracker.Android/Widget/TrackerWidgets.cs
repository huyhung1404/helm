using Android.Appwidget;
using Android.Content;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using RemoteViews = Android.Widget.RemoteViews;
using AndroidUri = Android.Net.Uri;
using ViewStates = Android.Views.ViewStates;
using Color = Android.Graphics.Color;

namespace Helm.Modules.Tracker;

/// <summary>
/// Draws the Tracker home-screen widgets and handles their taps. Runs in the app process, possibly without the
/// activity (Android starts the provider on its own), so services come from <see cref="HelmAndroidServices"/>.
/// </summary>
internal static class TrackerWidgets
{
    public const string ActionItem = "com.huyhung1404.helm.tracker.WIDGET_ITEM";
    public const string ActionNextWorkspace = "com.huyhung1404.helm.tracker.WIDGET_NEXT_WORKSPACE";
    public const string ActionSync = "com.huyhung1404.helm.tracker.WIDGET_SYNC";
    public const string ActionToggleCompact = "com.huyhung1404.helm.tracker.WIDGET_TOGGLE_COMPACT";
    public const string ActionToggleMenu = "com.huyhung1404.helm.tracker.WIDGET_TOGGLE_MENU";
    public const string ExtraItemId = "com.huyhung1404.helm.tracker.extra.ITEM_ID";
    public const string ExtraCommand = "com.huyhung1404.helm.tracker.extra.COMMAND";
    public const string CommandComplete = "complete";

    private const string PreferencesName = "helm_tracker_widgets";

    public static void RefreshAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(TrackerWidgetProvider))));
        if (ids is not { Length: > 0 }) return;
        foreach (var id in ids) Update(context, manager, id);
        NotifyListChanged(context, manager, ids);
    }

    /// <summary>Whether the launcher can place a widget when the app asks (Android 8+, most launchers).</summary>
    public static bool CanRequestPin(Context context)
    {
        try { return AppWidgetManager.GetInstance(context)?.IsRequestPinAppWidgetSupported == true; }
        catch (Exception) { return false; }
    }

    /// <summary>Asks the launcher to add the Tracker widget; the launcher shows its own confirmation.</summary>
    public static void RequestPin(Context context)
    {
        try
        {
            var provider = new ComponentName(context, Java.Lang.Class.FromType(typeof(TrackerWidgetProvider)));
            AppWidgetManager.GetInstance(context)?.RequestPinAppWidget(provider, null, null);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not ask the launcher to add the Tracker widget");
        }
    }

    /// <summary>Before Android 12 the rows come from <see cref="TrackerWidgetService"/>, which must be told to reload.</summary>
    public static void NotifyListChanged(Context context, AppWidgetManager manager, int[] ids)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            manager.NotifyAppWidgetViewDataChanged(ids, R.Id(context, "tracker_widget_list"));
    }

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        var model = Load(context, widgetId);
        var settings = Settings();
        var views = new RemoteViews(context.PackageName, R.Layout(context, "tracker_widget"));

        // Nothing open, or shrunk by the user: only the small button (with the count) on a see-through background.
        var compact = model.IsEmpty || IsCompact(context, widgetId);
        var menu = compact && IsMenuOpen(context, widgetId);
        views.SetViewVisibility(R.Id(context, "tracker_widget_full"), compact ? ViewStates.Gone : ViewStates.Visible);
        views.SetViewVisibility(R.Id(context, "tracker_widget_compact"), compact ? ViewStates.Visible : ViewStates.Gone);
        views.SetViewVisibility(R.Id(context, "tracker_widget_menu"), menu ? ViewStates.Visible : ViewStates.Gone);
        views.SetViewVisibility(R.Id(context, "tracker_widget_menu_grow"), menu && !model.IsEmpty ? ViewStates.Visible : ViewStates.Gone);
        // The count fills the round button; longer numbers get smaller so they are never cut.
        var count = model.OpenCount > 99 ? "99+" : model.OpenCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var countView = R.Id(context, "tracker_widget_count");
        views.SetTextViewText(countView, count);
        views.SetTextViewTextSize(countView, (int)Android.Util.ComplexUnitType.Sp, count.Length switch { 1 => 18, 2 => 15, _ => 12 });

        var bg = R.Id(context, "tracker_widget_bg");
        if (compact)
        {
            views.SetViewVisibility(bg, ViewStates.Gone);
        }
        else
        {
            views.SetViewVisibility(bg, ViewStates.Visible);
            views.SetInt(bg, "setColorFilter", TrackerWidgetStyle.Background(settings) is { } color ? unchecked((int)(color | 0xFF000000)) : 0);
            views.SetInt(bg, "setImageAlpha", TrackerWidgetStyle.Alpha(settings));
        }

        views.SetTextViewText(R.Id(context, "tracker_widget_title"), model.Title);
        views.SetTextViewText(R.Id(context, "tracker_widget_summary"), model.Summary);
        views.SetTextViewText(R.Id(context, "tracker_widget_empty"), model.EmptyText);
        if (TrackerWidgetStyle.Text(settings) is var (main, secondary))
        {
            views.SetTextColor(R.Id(context, "tracker_widget_title"), new Color(unchecked((int)main)));
            views.SetTextColor(R.Id(context, "tracker_widget_summary"), new Color(unchecked((int)secondary)));
            views.SetTextColor(R.Id(context, "tracker_widget_empty"), new Color(unchecked((int)secondary)));
        }

        var list = R.Id(context, "tracker_widget_list");
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            // Android 12+: the rows travel with the widget itself (no service, nothing to notify).
            var items = new RemoteViews.RemoteCollectionItems.Builder().SetHasStableIds(true)!.SetViewTypeCount(1)!;
            foreach (var row in model.Rows) items.AddItem(StableId(row.Id), BuildRow(context, row));
            views.SetRemoteAdapter(list, items.Build()!);
        }
        else
        {
            // Rows come from TrackerWidgetService. The data URI makes each widget's adapter intent distinct,
            // otherwise Android reuses one factory for every widget.
            var adapter = new Intent(context, typeof(TrackerWidgetService));
            adapter.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
            adapter.SetData(AndroidUri.Parse(adapter.ToUri(IntentUriType.Scheme)));
            views.SetRemoteAdapter(list, adapter);
        }
        views.SetEmptyView(list, R.Id(context, "tracker_widget_empty"));

        var item = new Intent(context, typeof(TrackerWidgetProvider)).SetAction(ActionItem)!;
        item.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        views.SetPendingIntentTemplate(list, PendingIntent.GetBroadcast(context, RequestCode(widgetId, 0), item, MutableFlags())!);

        views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_header"), Broadcast(context, widgetId, ActionNextWorkspace, 1));
        var sync = Broadcast(context, widgetId, ActionSync, 3);
        views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_sync"), sync);
        views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_menu_sync"), sync);
        var toggleCompact = Broadcast(context, widgetId, ActionToggleCompact, 4);
        views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_shrink"), toggleCompact);
        views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_menu_grow"), toggleCompact);
        views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_toggle"), Broadcast(context, widgetId, ActionToggleMenu, 5));

        if (OpenAppIntent(context, RequestCode(widgetId, 2)) is { } open)
        {
            views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_add"), open);
            views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_menu_add"), open);
            views.SetOnClickPendingIntent(R.Id(context, "tracker_widget_empty"), open);
        }
        manager.UpdateAppWidget(widgetId, views);
    }

    /// <summary>One row: title, details, and the priority-tinted circle that ticks the task off.</summary>
    public static RemoteViews BuildRow(Context context, TrackerWidgetRow row)
    {
        var views = new RemoteViews(context.PackageName, R.Layout(context, "tracker_widget_item"));
        var title = R.Id(context, "tracker_item_title");
        views.SetTextViewText(title, row.Title);

        var details = R.Id(context, "tracker_item_details");
        views.SetTextViewText(details, row.Details);
        views.SetViewVisibility(details, row.Details.Length > 0 ? ViewStates.Visible : ViewStates.Gone);
        if (TrackerWidgetStyle.Text(Settings()) is var (main, secondary))
        {
            views.SetTextColor(title, new Color(unchecked((int)main)));
            views.SetTextColor(details, new Color(unchecked((int)secondary)));
        }

        views.SetViewVisibility(R.Id(context, "tracker_item_amount"), ViewStates.Gone);

        var check = R.Id(context, "tracker_item_check");
        views.SetInt(check, "setColorFilter", PriorityColor(context, row.Priority).ToArgb());
        var fillIn = new Intent();
        fillIn.PutExtra(ExtraCommand, CommandComplete);
        fillIn.PutExtra(ExtraItemId, row.Id);
        views.SetOnClickFillInIntent(check, fillIn);
        return views;
    }

    /// <summary>A tap on sync: push and pull now, then redraw every widget (the receiver stays alive until done).</summary>
    public static void Sync(Context context, Android.Content.BroadcastReceiver.PendingResult? pending)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HelmAndroidServices.Current.GetRequiredService<Helm.Core.Sync.ISyncService>().SyncNowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log(ex, "Could not sync from the Tracker widget");
            }
            finally
            {
                RefreshAll(context);
                pending?.Finish();
            }
        });
    }

    public static void ToggleCompact(Context context, int widgetId)
    {
        var prefs = Preferences(context);
        prefs.Edit()?.PutBoolean(CompactKey(widgetId), !IsCompact(context, widgetId))?.PutBoolean(MenuKey(widgetId), false)?.Apply();
        RefreshAll(context);
    }

    public static void ToggleMenu(Context context, int widgetId)
    {
        Preferences(context).Edit()?.PutBoolean(MenuKey(widgetId), !IsMenuOpen(context, widgetId))?.Apply();
        RefreshAll(context);
    }

    private static bool IsCompact(Context context, int widgetId) => Preferences(context).GetBoolean(CompactKey(widgetId), false);

    private static bool IsMenuOpen(Context context, int widgetId) => Preferences(context).GetBoolean(MenuKey(widgetId), false);

    private static string CompactKey(int widgetId) => $"compact_{widgetId}";

    private static string MenuKey(int widgetId) => $"menu_{widgetId}";

    private static TrackerSettings Settings()
    {
        try
        {
            return HelmAndroidServices.Current.GetRequiredService<ISettingsStoreFactory>().Get<TrackerSettings>(TrackerIds.ModuleId).Current;
        }
        catch (Exception)
        {
            return new TrackerSettings();
        }
    }

    private static PendingIntent Broadcast(Context context, int widgetId, string action, int slot)
    {
        var intent = new Intent(context, typeof(TrackerWidgetProvider)).SetAction(action)!;
        intent.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        return PendingIntent.GetBroadcast(context, RequestCode(widgetId, slot), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    /// <summary>FNV-1a of the record id: stable across refreshes so the launcher can animate changes.</summary>
    public static long StableId(string id)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in id)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return unchecked((long)hash);
    }

    /// <summary>The model a widget shows now (also used by the list factory).</summary>
    public static TrackerWidgetModel Load(Context context, int widgetId)
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var store = services.GetRequiredService<TrackerStore>();
            var general = services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            var enabled = !general.EnabledModules.TryGetValue(TrackerIds.ModuleId, out var on) || on;
            return TrackerWidgetModel.Build(store, GetWorkspace(context, widgetId), enabled, store.Now, TimeZoneInfo.Local);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not load the Tracker widget data");
            return new TrackerWidgetModel(null, TrackerIds.DisplayName, "", "Open Helm to load your lists.", []);
        }
    }

    /// <summary>
    /// A tap on a row's circle: ticks the item off (recording its finish time, as in the app). Rows cannot open the
    /// app: that would start an activity from a broadcast, which Android blocks; the header's + button does it.
    /// </summary>
    public static void HandleItem(Context context, Intent intent)
    {
        var id = intent.GetStringExtra(ExtraItemId);
        if (intent.GetStringExtra(ExtraCommand) != CommandComplete || string.IsNullOrEmpty(id)) return;
        try
        {
            HelmAndroidServices.Current.GetRequiredService<TrackerStore>().Complete(id);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not complete a Tracker item from the widget");
        }
        RefreshAll(context);
    }

    public static void NextWorkspace(Context context, int widgetId)
    {
        try
        {
            var store = HelmAndroidServices.Current.GetRequiredService<TrackerStore>();
            SetWorkspace(context, widgetId, TrackerWidgetModel.NextWorkspace(store, Load(context, widgetId).WorkspaceId));
        }
        catch (Exception ex)
        {
            Log(ex, "Could not switch the Tracker widget workspace");
        }
        RefreshAll(context);
    }

    public static void Forget(Context context, int widgetId) =>
        Preferences(context).Edit()?.Remove(Key(widgetId))?.Remove(CompactKey(widgetId))?.Remove(MenuKey(widgetId))?.Apply();

    private static string? GetWorkspace(Context context, int widgetId) => Preferences(context).GetString(Key(widgetId), null);

    private static void SetWorkspace(Context context, int widgetId, string? workspaceId) =>
        Preferences(context).Edit()?.PutString(Key(widgetId), workspaceId)?.Apply();

    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;

    private static string Key(int widgetId) => $"workspace_{widgetId}";

    /// <summary>Opens Helm on the Tracker page (or brings the running app forward).</summary>
    internal static PendingIntent? OpenAppIntent(Context context, int requestCode)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, TrackerIds.ModuleId);
        return PendingIntent.GetActivity(context, requestCode, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    /// <summary>A list's click template must be mutable so each row can fill in its own extras.</summary>
    private static PendingIntentFlags MutableFlags() =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable
            : PendingIntentFlags.UpdateCurrent;

    private static int RequestCode(int widgetId, int slot) => widgetId * 8 + slot;

    internal static Color PriorityColor(Context context, TrackerPriority priority) => priority switch
    {
        TrackerPriority.Urgent => Colour(context, "tracker_widget_urgent"),
        TrackerPriority.High => Colour(context, "tracker_widget_accent"),
        _ => Colour(context, "tracker_widget_secondary"),
    };

    internal static Color Colour(Context context, string name) => new(context.GetColor(R.Get(context, name, "color")));

    private static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Tracker.Widget").LogWarning(ex, message);
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

    public static int Drawable(Context context, string name) => Get(context, name, "drawable");

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
