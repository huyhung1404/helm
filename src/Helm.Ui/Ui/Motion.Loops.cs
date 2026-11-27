using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;

namespace Helm.Core.Ui;

/// <summary>
/// Motion that goes on (a sync glyph turning, a placeholder breathing), played by the compositor: the render thread turns
/// or fades the element's own layer, so nothing runs on the UI thread, nothing is laid out again and only the element's
/// own small area is drawn again each frame. A loop stops while its element is hidden and when it leaves the screen.
/// Never loop with a style animation (<c>IterationCount="INFINITE"</c>): it sets a property on the UI thread every frame,
/// also while the element is hidden.
/// </summary>
public static partial class Motion
{
    /// <summary>Turns the element round and round while true (<c>core:Motion.Spin="{Binding IsBusy}"</c>).</summary>
    public static readonly AttachedProperty<bool> SpinProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Spin", typeof(Motion));

    /// <summary>The element fades out a little and back, again and again, while true (a placeholder while loading).</summary>
    public static readonly AttachedProperty<bool> PulseProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Pulse", typeof(Motion));

    public static void SetSpin(Control control, bool value) => control.SetValue(SpinProperty, value);

    public static bool GetSpin(Control control) => control.GetValue(SpinProperty);

    public static void SetPulse(Control control, bool value) => control.SetValue(PulseProperty, value);

    public static bool GetPulse(Control control) => control.GetValue(PulseProperty);

    /// <summary>One turn of <see cref="SpinProperty"/>.</summary>
    public static readonly TimeSpan SpinPeriod = TimeSpan.FromSeconds(1.1);

    /// <summary>One breath of <see cref="PulseProperty"/>.</summary>
    public static readonly TimeSpan PulsePeriod = TimeSpan.FromSeconds(1.6);

    private static readonly ConditionalWeakTable<Control, LoopState> Loops = new();

    /// <summary>How many elements loop right now.</summary>
    public static int RunningLoops { get; private set; }

    /// <summary>The element turns or breathes right now (shown, and asked to).</summary>
    public static bool IsLooping(Control control) => Loops.TryGetValue(control, out var state) && (state.Spinning || state.Pulsing);

    private sealed class LoopState
    {
        public bool Spinning;
        public bool Pulsing;
    }

    private static void HookLoops()
    {
        SpinProperty.Changed.AddClassHandler<Control>((control, _) => WatchLoop(control));
        PulseProperty.Changed.AddClassHandler<Control>((control, _) => WatchLoop(control));
    }

    private static void WatchLoop(Control control)
    {
        if (!Loops.TryGetValue(control, out _))
        {
            Loops.Add(control, new LoopState());
            control.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => UpdateLoop(control), DispatcherPriority.Loaded);
            control.DetachedFromVisualTree += (_, _) => UpdateLoop(control);
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty || e.Property == Visual.BoundsProperty) UpdateLoop(control);
            };
        }
        UpdateLoop(control);
    }

    private static void UpdateLoop(Control control)
    {
        if (!Loops.TryGetValue(control, out var state)) return;
        var visual = ElementComposition.GetElementVisual(control);
        var shown = visual is not null && control.IsVisible && TopLevel.GetTopLevel(control) is not null;
        var spin = shown && GetSpin(control);
        var pulse = shown && GetPulse(control);

        if (visual is not null)
        {
            // Turn round the middle (the size can change while it turns).
            if (spin) visual.CenterPoint = new Vector3D(control.Bounds.Width / 2, control.Bounds.Height / 2, 0);
            if (spin != state.Spinning)
            {
                if (spin)
                {
                    var turn = visual.Compositor.CreateScalarKeyFrameAnimation();
                    turn.InsertKeyFrame(0f, 0f, new LinearEasing());
                    turn.InsertKeyFrame(1f, (float)(2 * Math.PI), new LinearEasing());
                    turn.Duration = SpinPeriod;
                    turn.IterationBehavior = AnimationIterationBehavior.Forever;
                    visual.StartAnimation("RotationAngle", turn);
                }
                else
                {
                    visual.StopAnimation("RotationAngle");
                    visual.RotationAngle = 0;
                }
            }
            if (pulse != state.Pulsing)
            {
                if (pulse)
                {
                    var breath = visual.Compositor.CreateScalarKeyFrameAnimation();
                    breath.InsertKeyFrame(0f, 1f, new SineEaseInOut());
                    breath.InsertKeyFrame(0.5f, 0.35f, new SineEaseInOut());
                    breath.InsertKeyFrame(1f, 1f, new SineEaseInOut());
                    breath.Duration = PulsePeriod;
                    breath.IterationBehavior = AnimationIterationBehavior.Forever;
                    visual.StartAnimation("Opacity", breath);
                }
                else
                {
                    visual.StopAnimation("Opacity");
                    visual.Opacity = (float)control.Opacity;
                }
            }
        }
        else
        {
            spin = pulse = false;
        }

        RunningLoops += (spin ? 1 : 0) + (pulse ? 1 : 0) - (state.Spinning ? 1 : 0) - (state.Pulsing ? 1 : 0);
        state.Spinning = spin;
        state.Pulsing = pulse;
    }
}

/// <summary>Moves that the compositor plays once (a layer sliding into place), with no layout while they play.</summary>
public static partial class Motion
{
    /// <summary>How long <see cref="SlideFrom"/> takes.</summary>
    public static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// <paramref name="control"/> was just laid out where it belongs; it slides there from <paramref name="dx"/> pixels
    /// to the side. Only its layer moves (on the render thread): use it instead of animating a width or a margin, which
    /// lays the whole page out again every frame.
    /// </summary>
    public static void SlideFrom(Control control, double dx)
    {
        if (dx == 0 || ElementComposition.GetElementVisual(control) is not { } visual) return;
        var slide = visual.Compositor.CreateVector3DKeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3D(dx, 0, 0), new LinearEasing());
        slide.InsertKeyFrame(1f, new Vector3D(0, 0, 0), new CubicEaseOut());
        slide.Duration = SlideDuration;
        visual.StartAnimation("Translation", slide);
    }
}
