using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Helm.Ai.Assistant;
using Helm.Core.Platform;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Ask Helm's edge handle: a thin line on the screen's edge over the other apps (<see cref="EdgeHandleView"/>), placed
/// from <see cref="AskHelmOpenSettings"/> by <see cref="EdgeHandleLayout"/>. Over a full-screen app it turns invisible and
/// lets touches through, but its window stays to hear when full screen ends: no polling. Main thread only.
/// </summary>
public sealed class AskHelmEdgeHandle(ISettingsStoreFactory settings, ILogger<AskHelmEdgeHandle> logger)
{
    private readonly ISettingsStore<AskHelmOpenSettings> _store = settings.Get<AskHelmOpenSettings>(AskHelmOpenSettings.StoreId);
    private readonly Context _context = AndroidApp.Context;
    private IWindowManager? _windows;
    private EdgeHandleView? _view;
    private WindowManagerLayoutParams? _params;
    private TurnWatcher? _turns;
    private bool _fullScreen;

    /// <summary>Whether the handle wants a window now (on, the tool on, the permission given).</summary>
    public bool Wanted => EdgeHandleRules.Wanted(_store.Current, QuickCaptureModule.IsModuleEnabled(settings, QuickCaptureModule.ModuleId),
        OverlayPermission.Granted(_context));

    /// <summary>Adds, moves or removes the handle as the settings say now. Harmless to call often.</summary>
    public void Update()
    {
        try
        {
            if (!Wanted)
            {
                Remove();
                return;
            }
            var s = _store.Current;
            if (_view is null) Add(s);
            else Place(s);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show Ask Helm's edge handle");
        }
    }

    public void Remove()
    {
        if (_turns is not null) _context.UnregisterComponentCallbacks(_turns);
        _turns = null;
        if (_view is not { } view) return;
        _view = null;
        _params = null;
        try
        {
            Windows().RemoveView(view);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Already gone.
        }
    }

    private void Add(AskHelmOpenSettings s)
    {
        _fullScreen = false;
        var view = new EdgeHandleView(_context, s.EdgeSide, Open, OnFullScreen);
        var p = new WindowManagerLayoutParams(0, 0, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutInScreen, Format.Translucent);
        Frame(s, p);
        Windows().AddView(view, p);
        _view = view;
        _params = p;
        Show(s);
        _turns = new TurnWatcher(this);
        _context.RegisterComponentCallbacks(_turns);
    }

    private void Place(AskHelmOpenSettings s)
    {
        if (_view is null || _params is null) return;
        _view.Side = s.EdgeSide;
        Frame(s, _params);
        Show(s);
    }

    private void Frame(AskHelmOpenSettings s, WindowManagerLayoutParams p)
    {
        var metrics = _context.Resources!.DisplayMetrics!;
        var frame = EdgeHandleLayout.Place(metrics.HeightPixels, metrics.Density, s.EdgeHeightDp, s.EdgePositionPercent);
        p.Width = frame.Width;
        p.Height = frame.Height;
        p.Gravity = GravityFlags.Top | (s.EdgeSide == EdgeSide.Right ? GravityFlags.Right : GravityFlags.Left);
        p.X = 0;
        p.Y = frame.Top;
    }

    /// <summary>Drawn and touchable, or invisible with touches passing to the app below (alpha 0 lets them through).</summary>
    private void Show(AskHelmOpenSettings s)
    {
        if (_view is null || _params is not { } p) return;
        var shown = EdgeHandleRules.Shown(s, _fullScreen);
        _view.Hidden = !shown;
        p.Alpha = shown ? 1f : 0f;
        if (shown) p.Flags &= ~WindowManagerFlags.NotTouchable;
        else p.Flags |= WindowManagerFlags.NotTouchable;
        try
        {
            Windows().UpdateViewLayout(_view, p);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Removed meanwhile.
        }
    }

    private void OnFullScreen(bool fullScreen)
    {
        if (fullScreen == _fullScreen) return;
        _fullScreen = fullScreen;
        Show(_store.Current);
    }

    private void Open()
    {
        try
        {
            _context.StartActivity(AskHelmActivity.OpenIntent(_context, AskHelmOpenMode.Listen));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The edge handle could not open Ask Helm");
        }
    }

    /// <summary>The phone turned (the room changed) or the theme changed (the accent).</summary>
    private void OnTurned()
    {
        _view?.UpdateColour();
        Update();
    }

    private IWindowManager Windows() => _windows ??= _context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;

    private sealed class TurnWatcher(AskHelmEdgeHandle handle) : Java.Lang.Object, IComponentCallbacks
    {
        private readonly Handler _main = new(Looper.MainLooper!);

        public void OnConfigurationChanged(Configuration newConfig) => _main.Post(handle.OnTurned);

        public void OnLowMemory()
        {
        }
    }
}
