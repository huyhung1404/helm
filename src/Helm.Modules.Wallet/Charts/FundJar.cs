using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Helm.Modules.Wallet;

/// <summary>
/// A fund drawn as a honey pot: the water is what is left of it (0–1), two slow waves move on its surface, and the
/// water drops smoothly when a transaction comes off the fund. The outline says the status (amber low, orange used up,
/// red over) next to the words the pages print, so colour never carries it alone. Without Windows' animations the
/// waves stand still and the level jumps.
/// </summary>
public sealed class FundJar : Grid
{
    // The pot in a 100 × 110 box: a small lid, then a round belly from y 10 to 108.
    private const double Bottom = 108, BodyHeight = 98;
    private static readonly Geometry BodyGeometry = Freeze(Geometry.Parse("M50,10 C79,10 98,23 98,53 C98,87 79,108 50,108 C21,108 2,87 2,53 C2,23 21,10 50,10 Z"));
    private static readonly Geometry WaveGeometry = Freeze(Geometry.Parse("M0,4 Q12.5,0 25,4 T50,4 T75,4 T100,4 T125,4 T150,4 V14 H0 Z"));
    private static readonly Geometry WaveBackGeometry = Freeze(Geometry.Parse("M0,4 Q12.5,8 25,4 T50,4 T75,4 T100,4 T125,4 T150,4 V14 H0 Z"));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(FundJar), new PropertyMetadata(0.0, (d, e) => ((FundJar)d).AnimateLevel((double)e.NewValue)));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(int), typeof(FundJar), new PropertyMetadata(0, (d, _) => ((FundJar)d).UpdateLook()));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(FundStatus), typeof(FundJar), new PropertyMetadata(FundStatus.OnTrack, (d, _) => ((FundJar)d).UpdateLook()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FundJar), new PropertyMetadata("", (d, e) => ((FundJar)d)._label.Text = e.NewValue as string ?? ""));

    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(
        nameof(TextSize), typeof(double), typeof(FundJar), new PropertyMetadata(13.0, (d, e) => ((FundJar)d)._label.FontSize = (double)e.NewValue));

    /// <summary>The water drawn now (animated toward <see cref="Level"/>).</summary>
    private static readonly DependencyProperty ShownLevelProperty = DependencyProperty.Register(
        "ShownLevel", typeof(double), typeof(FundJar), new PropertyMetadata(0.0, (d, _) => ((FundJar)d).PlaceWater()));

    private readonly Rectangle _lid = new() { RadiusX = 2, RadiusY = 2, Width = 36, Height = 9 };
    private readonly Path _body = new() { Data = BodyGeometry, StrokeThickness = 2.5 };
    private readonly Canvas _water = new() { Width = 100, Height = 110, Clip = BodyGeometry };
    private readonly Rectangle _fill = new() { Width = 100 };
    private readonly Path _wave = new() { Data = WaveGeometry };
    private readonly Path _waveBack = new() { Data = WaveBackGeometry, Opacity = 0.55 };
    private readonly TranslateTransform _waveMove = new();
    private readonly TranslateTransform _waveBackMove = new();
    private readonly Rectangle _shine = new() { Width = 8, Height = 40, RadiusX = 4, RadiusY = 4 };
    private readonly TextBlock _label = new() { FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public FundJar()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        _wave.RenderTransform = _waveMove;
        _waveBack.RenderTransform = _waveBackMove;
        _water.Children.Add(_fill);
        _water.Children.Add(_waveBack);
        _water.Children.Add(_wave);

        var canvas = new Canvas { Width = 100, Height = 110 };
        Canvas.SetLeft(_lid, 32);
        canvas.Children.Add(_lid);
        canvas.Children.Add(_water);
        Canvas.SetLeft(_shine, 15);
        Canvas.SetTop(_shine, 30);
        canvas.Children.Add(_shine);
        canvas.Children.Add(_body);
        Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = canvas });
        // The words sit on the belly, a little below the middle of the box.
        _label.Margin = new Thickness(0, 8, 0, 0);
        _label.FontSize = TextSize;
        Children.Add(_label);

        Loaded += (_, _) =>
        {
            UpdateLook();
            StartWaves();
        };
        Unloaded += (_, _) => StopWaves();
        PlaceWater();
    }

    /// <summary>What is left, 0–1.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>The fund's colour slot (<see cref="WalletPalette"/>).</summary>
    public int Color
    {
        get => (int)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public FundStatus Status
    {
        get => (FundStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>The words on the belly ("20%", "Over").</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double TextSize
    {
        get => (double)GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    private static bool Animates => SystemParameters.ClientAreaAnimation;

    private void AnimateLevel(double level)
    {
        level = Math.Clamp(double.IsNaN(level) ? 0 : level, 0, 1);
        if (!IsLoaded || !Animates)
        {
            BeginAnimation(ShownLevelProperty, null);
            SetValue(ShownLevelProperty, level);
            return;
        }
        BeginAnimation(ShownLevelProperty, new DoubleAnimation(level, TimeSpan.FromSeconds(0.9))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void PlaceWater()
    {
        var level = (double)GetValue(ShownLevelProperty);
        var top = Bottom - level * BodyHeight;
        Canvas.SetTop(_fill, top);
        _fill.Height = Math.Max(0, 110 - top);
        Canvas.SetTop(_wave, top - 6);
        Canvas.SetTop(_waveBack, top - 8);
        var visible = level > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        _wave.Visibility = visible;
        _waveBack.Visibility = visible;
        // Over the water the words are white; over the empty glass, in the text colour.
        _label.Foreground = level > 0.55 ? Brushes.White : WalletTheme.Text(this, "TextFillColorPrimaryBrush", Brushes.Black);
    }

    private void UpdateLook()
    {
        var water = WalletTheme.Brush(Color, 0xD9, this);
        _fill.Fill = water;
        _wave.Fill = water;
        _waveBack.Fill = water;
        _lid.Fill = WalletTheme.Text(this, "ControlStrongFillColorDefaultBrush", Brushes.Gray);
        _shine.Fill = WalletTheme.Frozen(0x22FFFFFF);
        _body.Fill = WalletTheme.Text(this, "SubtleFillColorSecondaryBrush", Brushes.Transparent);
        var dark = WalletTheme.IsDark(this);
        _body.Stroke = Status switch
        {
            FundStatus.Over => WalletTheme.Text(this, "SystemFillColorCriticalBrush", Brushes.Red),
            FundStatus.UsedUp => WalletTheme.Frozen(dark ? 0xFFFFB86Bu : 0xFFA8461Au),
            FundStatus.Low => WalletTheme.Text(this, "SystemFillColorCautionBrush", Brushes.Goldenrod),
            _ => WalletTheme.Text(this, "ControlStrongStrokeColorDefaultBrush", Brushes.Gray),
        };
        PlaceWater();
    }

    private void StartWaves()
    {
        if (!Animates) return;
        _waveMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -50, TimeSpan.FromSeconds(3.2)) { RepeatBehavior = RepeatBehavior.Forever });
        _waveBackMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-50, 0, TimeSpan.FromSeconds(5.5)) { RepeatBehavior = RepeatBehavior.Forever });
        AnimateLevel(Level);
    }

    private void StopWaves()
    {
        _waveMove.BeginAnimation(TranslateTransform.XProperty, null);
        _waveBackMove.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
