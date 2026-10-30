using Android.Appwidget;
using Android.Content;
using Android.Widget;
using ServiceAttribute = Android.App.ServiceAttribute;

namespace Helm.Modules.Vocab;

/// <summary>
/// Feeds the widget's answer list before Android 12 (Android asks it for lines whenever the widget is told its data
/// changed). From Android 12 on, <see cref="VocabWidgets.Update"/> puts the lines into the widget directly.
/// </summary>
[Service(Permission = "android.permission.BIND_REMOTEVIEWS", Exported = false)]
public sealed class VocabWidgetService : RemoteViewsService
{
    public override IRemoteViewsFactory? OnGetViewFactory(Intent? intent) =>
        new VocabWidgetFactory(ApplicationContext!,
            intent?.GetIntExtra(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId) ?? AppWidgetManager.InvalidAppwidgetId);
}

internal sealed class VocabWidgetFactory(Context context, int widgetId) : Java.Lang.Object, RemoteViewsService.IRemoteViewsFactory
{
    private IReadOnlyList<VocabWidgetLine> _lines = [];
    private VocabWidgets.Colours? _colours;

    public int Count => _lines.Count;

    public bool HasStableIds => false;

    public RemoteViews? LoadingView => null;

    public int ViewTypeCount => 1;

    public long GetItemId(int position) => position;

    public RemoteViews? GetViewAt(int position) =>
        position < _lines.Count && _colours is not null ? VocabWidgets.BuildLine(context, _lines[position], _colours) : null;

    public void OnCreate() { }

    /// <summary>Called on a binder thread; reading the local replica here is allowed and quick.</summary>
    public void OnDataSetChanged() => (_lines, _colours) = VocabWidgets.LinesOf(context, widgetId);

    public void OnDestroy() => _lines = [];
}
