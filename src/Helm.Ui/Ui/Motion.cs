using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Helm.Core.Ui;

/// <summary>
/// How things come on screen in Helm: they grow into place (a popup scale, from a little smaller to full size) as
/// they appear, quickly and eased. <see cref="Enter"/> plays it once; <c>core:Motion.EnterOnShow="True"</c> plays it
/// every time a control is shown again (a tab, a folded section, a task opened). A section that opens downwards grows
/// from its top edge (<see cref="FromTopProperty"/>); a dialog from its centre. A whole page opened in the shell does not
/// grow: it comes into focus over its still backdrop (<see cref="EnterPage"/>).
/// </summary>
public static class Motion
{
    public static readonly AttachedProperty<bool> EnterOnShowProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("EnterOnShow", typeof(Motion));

    /// <summary>Grow from the top edge (a section opening under its header) instead of the centre.</summary>
    public static readonly AttachedProperty<bool> FromTopProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FromTop", typeof(Motion));

    /// <summary>For a disclosure arrow (Panel.turn): true while its section is open; sets the .open / .closing classes the turn styles play.</summary>
    public static readonly AttachedProperty<bool> TurnProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Turn", typeof(Motion));

    public static void SetTurn(Control control, bool value) => control.SetValue(TurnProperty, value);

    public static bool GetTurn(Control control) => control.GetValue(TurnProperty);

    public static void SetEnterOnShow(Control control, bool value) => control.SetValue(EnterOnShowProperty, value);

    public static bool GetEnterOnShow(Control control) => control.GetValue(EnterOnShowProperty);

    public static void SetFromTop(Control control, bool value) => control.SetValue(FromTopProperty, value);

    public static bool GetFromTop(Control control) => control.GetValue(FromTopProperty);

    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(260);

    static Motion()
    {
        EnterOnShowProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.PropertyChanged -= OnPropertyChanged;
            if (e.NewValue is true) control.PropertyChanged += OnPropertyChanged;
        });
        TurnProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            var open = e.NewValue is true;
            control.Classes.Set("open", open);
            control.Classes.Set("closing", !open && e.OldValue is true);
        });
    }

    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true && sender is Control control) Enter(control);
    }

    /// <summary>Grows <paramref name="control"/> into place from <paramref name="from"/> of its size (only fades if it is transformed otherwise).</summary>
    public static void Enter(Control control, double from = 0.94)
    {
        if (control.RenderTransform is null) control.RenderTransform = new ScaleTransform();
        // The centre unless told otherwise (FromTop, or an origin the caller set).
        // Avalonia turns RenderTransform into its standard group (scale, skew, rotate, translate) the first time a
        // transform is animated, so a group can be scaled too.
        var scales = control.RenderTransform is ScaleTransform or TransformGroup;
        if (scales && GetFromTop(control)) control.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
        var start = new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d) } };
        var end = new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d) } };
        if (scales)
        {
            start.Setters.Add(new Setter(ScaleTransform.ScaleXProperty, from));
            start.Setters.Add(new Setter(ScaleTransform.ScaleYProperty, from));
            end.Setters.Add(new Setter(ScaleTransform.ScaleXProperty, 1d));
            end.Setters.Add(new Setter(ScaleTransform.ScaleYProperty, 1d));
        }
        var animation = new Animation { Duration = Duration, Easing = new CubicEaseOut(), FillMode = FillMode.Backward, Children = { start, end } };
        _ = animation.RunAsync(control);
    }

    /// <summary>How far out of focus a page starts (<see cref="EnterPage"/>).</summary>
    public const double PageBlur = 16;

    /// <summary>
    /// A whole page coming on screen (a tool opened in the shell): its <see cref="Backdrop"/> stays put and everything on
    /// it comes into focus, from blurred and clear to sharp. Nothing scales: a page that shrinks or fades as a whole shows
    /// the empty (black) window behind it at its edges. Returns what was animated.
    /// </summary>
    public static IReadOnlyList<Control> EnterPage(Control page)
    {
        var targets = PageLayers(page);
        foreach (var target in targets)
        {
            var start = new KeyFrame
            {
                Cue = new Cue(0),
                Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(Visual.EffectProperty, new BlurEffect { Radius = PageBlur }) },
            };
            var end = new KeyFrame
            {
                Cue = new Cue(1),
                Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(Visual.EffectProperty, new BlurEffect { Radius = 0 }) },
            };
            // Backward: the start shows before the first frame; afterwards the page has no effect left (a blur of 0 still costs).
            var animation = new Animation { Duration = Duration, Easing = new CubicEaseOut(), FillMode = FillMode.Backward, Children = { start, end } };
            _ = animation.RunAsync(target);
        }
        return targets;
    }

    /// <summary>The layers drawn over the page's backdrop (its siblings), or the page itself when it has none.</summary>
    internal static IReadOnlyList<Control> PageLayers(Control page)
    {
        if (FindBackdrop(page) is { Parent: Panel panel } backdrop)
            return [.. panel.Children.Where(c => !ReferenceEquals(c, backdrop))];
        return [page];
    }

    private static Backdrop? FindBackdrop(Control control)
    {
        // A page puts its backdrop near the top (UserControl → Panel → Backdrop); look a few levels down only.
        var level = new List<Control> { control };
        for (var depth = 0; depth < 4 && level.Count > 0; depth++)
        {
            if (level.OfType<Backdrop>().FirstOrDefault() is { } found) return found;
            level = [.. level.SelectMany(Children)];
        }
        return null;
    }

    private static IEnumerable<Control> Children(Control control) => control switch
    {
        Panel panel => panel.Children,
        ContentControl { Content: Control child } => [child],
        Decorator { Child: { } child } => [child],
        _ => [],
    };
}
