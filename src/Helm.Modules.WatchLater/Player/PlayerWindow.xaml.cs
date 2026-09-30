using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Helm.Core.Settings;

namespace Helm.Modules.WatchLater.Player;

/// <summary>
/// The mini player (Windows): the video in a small window that stays on top of the others, opened only when asked
/// (the player's Mini player button). Its place and size are remembered; Back to Helm (or a double-click on its bar,
/// or Esc) returns the video to the Watch Later page.
/// </summary>
public partial class PlayerWindow
{
    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly Action<string?> _dock;

    /// <param name="dock">Called with the video playing when the user goes back to Helm.</param>
    internal PlayerWindow(ISettingsStore<WatchLaterSettings> settings, Action<string?> dock)
    {
        _settings = settings;
        _dock = dock;
        InitializeComponent();
        PlaceWindow();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Dock();
        };
    }

    internal PlayerView Player => View;

    internal void ShowTitle(string title)
    {
        Title = title;
        MiniTitle.Text = title;
    }

    internal void ShowRate(double rate) => SpeedButton.Content = SpeedText(rate);

    internal static string SpeedText(double rate) => rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";

    /// <summary>Raised when the speed button picks the next speed.</summary>
    internal event EventHandler<double>? SpeedPicked;

    private double _rate = 1;

    internal void SetRate(double rate)
    {
        _rate = rate;
        ShowRate(rate);
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        var speeds = PlayerView.Speeds;
        var index = speeds.ToList().FindIndex(s => Math.Abs(s - _rate) < 0.01);
        var next = speeds[(index + 1) % speeds.Count];
        SetRate(next);
        SpeedPicked?.Invoke(this, next);
    }

    private void Dock_Click(object sender, RoutedEventArgs e) => Dock();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Dock()
    {
        var id = View.CurrentId;
        View.Flush();
        _dock(id);
    }

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Dock();
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException) { }
    }

    private void PlaceWindow()
    {
        if (PlayerView.TestMode)
        {
            // Never on the desktop of whoever runs a test instance.
            ShowActivated = false;
            Topmost = false;
            Left = -4000;
            Top = 0;
            return;
        }
        if (_settings.Current.MiniBounds is { Length: 4 } b && IsOnScreen(b))
        {
            Left = b[0];
            Top = b[1];
            Width = b[2];
            Height = b[3];
            return;
        }
        // Bottom-right of the screen, 16:9 video under the bar.
        var area = SystemParameters.WorkArea;
        Width = 432;
        Height = 432 * 9 / 16.0 + 32;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }

    private static bool IsOnScreen(double[] b) =>
        b[2] >= 200 && b[3] >= 120
        && b[0] + b[2] > SystemParameters.VirtualScreenLeft + 40 && b[0] < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
        && b[1] >= SystemParameters.VirtualScreenTop - 8 && b[1] < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    protected override void OnClosing(CancelEventArgs e)
    {
        View.Flush();
        if (!PlayerView.TestMode && WindowState == WindowState.Normal && !double.IsNaN(Left) && !double.IsNaN(Top))
        {
            var bounds = new[] { Left, Top, Width, Height };
            _settings.Update(s => s.MiniBounds = bounds);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        View.Release();
        base.OnClosed(e);
    }
}
