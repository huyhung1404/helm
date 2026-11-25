using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Helm.Core.Ui;

/// <summary>
/// How things come on screen in Helm: they fade in and rise a few pixels, quickly and eased. <see cref="Enter"/> plays
/// it once; <c>core:Motion.EnterOnShow="True"</c> plays it every time a control is shown again (a tab, a folded
/// section, a task opened, a page shown in the WPF shell).
/// </summary>
public static class Motion
{
    public static readonly AttachedProperty<bool> EnterOnShowProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("EnterOnShow", typeof(Motion));

    public static void SetEnterOnShow(Control control, bool value) => control.SetValue(EnterOnShowProperty, value);

    public static bool GetEnterOnShow(Control control) => control.GetValue(EnterOnShowProperty);

    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(280);

    static Motion() =>
        EnterOnShowProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.PropertyChanged -= OnPropertyChanged;
            if (e.NewValue is true) control.PropertyChanged += OnPropertyChanged;
        });

    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true && sender is Control control) Enter(control);
    }

    /// <summary>Fades <paramref name="control"/> in, rising <paramref name="rise"/> px (only fades if it is transformed otherwise).</summary>
    public static void Enter(Control control, double rise = 12)
    {
        if (control.RenderTransform is null) control.RenderTransform = new TranslateTransform();
        var moves = control.RenderTransform is TranslateTransform;
        var from = new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d) } };
        var to = new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d) } };
        if (moves)
        {
            from.Setters.Add(new Setter(TranslateTransform.YProperty, rise));
            to.Setters.Add(new Setter(TranslateTransform.YProperty, 0d));
        }
        var animation = new Animation { Duration = Duration, Easing = new CubicEaseOut(), FillMode = FillMode.Backward, Children = { from, to } };
        _ = animation.RunAsync(control);
    }
}
