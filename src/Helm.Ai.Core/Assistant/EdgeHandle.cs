namespace Helm.Ai.Assistant;

/// <summary>Where the edge handle's window goes on a screen, in pixels.</summary>
/// <param name="Top">The window's top, from the top of the room it has.</param>
/// <param name="Width">The touch strip (wider than the line drawn in it).</param>
/// <param name="Height">The line's length, which is also the strip Android keeps from the Back gesture.</param>
public readonly record struct EdgeHandleFrame(int Top, int Width, int Height);

/// <summary>
/// The edge handle's size and place (Ask Helm › Ways to open): a 4dp line, 3dp from the edge, in a 20dp strip that takes
/// the touches, widened to <see cref="DragStripDp"/> while a finger drags the line. Platform-free, so it is tested off
/// the phone.
/// </summary>
public static class EdgeHandleLayout
{
    public const int StripDp = 20;
    public const int LineDp = 4;
    public const int LineInsetDp = 3;

    /// <summary>The line grows this much wider as the drag nears opening.</summary>
    public const int LineGrowDp = 4;

    /// <summary>
    /// The strip while a finger drags: wide enough for the line, at its widest and its insets on both sides, to follow
    /// the finger all of <see cref="EdgeHandleGesture.MaxPullDp"/> inward (well inside the 200dp Android leaves an app).
    /// </summary>
    public const int DragStripDp = EdgeHandleGesture.MaxPullDp + LineDp + LineGrowDp + 2 * LineInsetDp;

    /// <summary>The strip's width now, in pixels.</summary>
    public static int StripPx(bool dragging, float density) => Px(dragging ? DragStripDp : StripDp, density);

    /// <param name="roomPx">The height the window can use (the screen's).</param>
    /// <param name="density">Pixels per dp.</param>
    public static EdgeHandleFrame Place(int roomPx, float density, int heightDp, int positionPercent)
    {
        var height = Px(AskHelmOpenSettings.ClampHeight(heightDp), density);
        height = Math.Min(height, Math.Max(0, roomPx));
        var percent = AskHelmOpenSettings.ClampPosition(positionPercent);
        // 0% would be the top of the room, 100% its bottom: the handle never leaves the screen.
        var top = (int)Math.Round((roomPx - height) * (percent / 100.0));
        return new EdgeHandleFrame(Math.Max(0, top), Px(StripDp, density), height);
    }

    public static int Px(double dp, float density) => (int)Math.Round(dp * density);
}

/// <summary>What a touch on the edge handle did.</summary>
public enum EdgeHandleAction
{
    None,

    /// <summary>Open Ask Helm's card, listening.</summary>
    Open,
}

/// <summary>
/// The edge handle's touch: a drag that travels 40dp inward opens the card (so a Back swipe that starts beside it never
/// does), and so does a plain tap. A drag that went less far, or outward, does nothing. One gesture opens it once.
/// Coordinates are the screen's, in pixels.
/// </summary>
public sealed class EdgeHandleGesture(EdgeSide side, float density)
{
    public const int OpenDistanceDp = 40;

    /// <summary>The line follows the finger this far at most.</summary>
    public const int MaxPullDp = 44;

    /// <summary>Movement up to this still counts as a tap.</summary>
    public const int TapSlopDp = 6;

    private float? _downX, _downY;
    private bool _moved, _opened;

    public EdgeSide Side { get; } = side;

    /// <summary>How far inward the finger is from where it went down (negative: outward).</summary>
    public float Inward { get; private set; }

    /// <summary>How far the line is drawn pulled inward now, in pixels.</summary>
    public float Pull => Math.Clamp(Inward, 0, MaxPullDp * density);

    /// <summary>How far along the drag is to opening, 0–1.</summary>
    public float Progress => Math.Clamp(Inward / (OpenDistanceDp * density), 0, 1);

    public bool IsDown => _downX is not null;

    public void Down(float x, float y)
    {
        _downX = x;
        _downY = y;
        _moved = _opened = false;
        Inward = 0;
    }

    public EdgeHandleAction Move(float x, float y)
    {
        if (_downX is not { } downX || _downY is not { } downY) return EdgeHandleAction.None;
        Inward = Side == EdgeSide.Right ? downX - x : x - downX;
        var slop = TapSlopDp * density;
        if (Math.Abs(x - downX) > slop || Math.Abs(y - downY) > slop) _moved = true;
        if (_opened || Inward < OpenDistanceDp * density) return EdgeHandleAction.None;
        _opened = true;
        return EdgeHandleAction.Open;
    }

    public EdgeHandleAction Up(float x, float y)
    {
        if (!IsDown) return EdgeHandleAction.None;
        var action = Move(x, y);
        var tap = !_moved && !_opened;
        Reset();
        return action == EdgeHandleAction.Open || tap ? EdgeHandleAction.Open : EdgeHandleAction.None;
    }

    /// <summary>The system took the gesture (a Back swipe, the screen turned off).</summary>
    public void Cancel() => Reset();

    private void Reset()
    {
        _downX = _downY = null;
        Inward = 0;
    }
}

/// <summary>Whether the edge handle is on screen now.</summary>
public static class EdgeHandleRules
{
    /// <summary>The handle exists (its window is added) only while it is on and Helm may draw over other apps.</summary>
    public static bool Wanted(AskHelmOpenSettings settings, bool moduleOn, bool canDraw) => settings.EdgeHandle && moduleOn && canDraw;

    /// <summary>
    /// Drawn and touchable: hidden over a full-screen app when the user asked so (the window stays, invisible and
    /// passing touches through, to hear when full screen ends).
    /// </summary>
    public static bool Shown(AskHelmOpenSettings settings, bool fullScreen) => !(fullScreen && settings.HideOverFullScreen);
}
