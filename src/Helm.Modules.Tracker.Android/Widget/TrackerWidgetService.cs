using Android.Appwidget;
using Android.Content;
using Android.Widget;
using ServiceAttribute = Android.App.ServiceAttribute;

namespace Helm.Modules.Tracker;

/// <summary>
/// Feeds the widget's list before Android 12 (Android asks it for rows whenever the widget is told its data
/// changed). From Android 12 on, <see cref="TrackerWidgets.Update"/> puts the rows into the widget directly.
/// </summary>
[Service(Permission = "android.permission.BIND_REMOTEVIEWS", Exported = false)]
public sealed class TrackerWidgetService : RemoteViewsService
{
    public override IRemoteViewsFactory? OnGetViewFactory(Intent? intent) =>
        new TrackerWidgetFactory(ApplicationContext!,
            intent?.GetIntExtra(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId) ?? AppWidgetManager.InvalidAppwidgetId);
}

internal sealed class TrackerWidgetFactory(Context context, int widgetId) : Java.Lang.Object, RemoteViewsService.IRemoteViewsFactory
{
    private IReadOnlyList<TrackerWidgetRow> _rows = [];

    public int Count => _rows.Count;

    public bool HasStableIds => true;

    public RemoteViews? LoadingView => null;

    public int ViewTypeCount => 1;

    public long GetItemId(int position) => position < _rows.Count ? TrackerWidgets.StableId(_rows[position].Id) : position;

    public RemoteViews? GetViewAt(int position) => position < _rows.Count ? TrackerWidgets.BuildRow(context, _rows[position]) : null;

    public void OnCreate() { }

    /// <summary>Called on a binder thread; reading the local replica here is allowed and quick.</summary>
    public void OnDataSetChanged() => _rows = TrackerWidgets.Load(context, widgetId).Rows;

    public void OnDestroy() => _rows = [];
}
