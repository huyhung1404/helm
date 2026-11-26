using Avalonia;
using Avalonia.Media;
using Helm.Modules.Keyboard;
using Helm.Modules.SlackBot;
using Helm.Modules.WindowZones;

namespace Helm.ToolIcons;

/// <summary>
/// Each tool's own icon, as Helm draws it. The phone app's Avalonia builders are linked in (see the .csproj); the
/// Windows-only tools have WPF builders only, so their same shapes are drawn here the same way.
/// </summary>
public sealed class ToolIcons : Helm.Home.IToolIcons
{
    public static IImage? Of(string id) => id switch
    {
        "habits" => Helm.Modules.Habits.Ui.HabitsIcon.Image,
        "missions" => Helm.Modules.Missions.MissionsIcon.Image,
        "notes" => Helm.Modules.Notes.NotesIcon.Image,
        "novel-reader" => Helm.Modules.NovelReader.NovelReaderIcon.Image,
        "quick-capture" => Helm.Modules.QuickCapture.QuickCaptureIcon.Image,
        "scratch" => Helm.Modules.Scratch.ScratchIcon.Image,
        "ssh" => Helm.Modules.Ssh.SshIcon.Image,
        "tracker" => Helm.Modules.Tracker.TrackerIcon.Image,
        "vocab" => Helm.Modules.Vocab.VocabIcon.Image,
        "wallet" => Helm.Modules.Wallet.WalletIcon.Image,
        "workspace" => Helm.Modules.Workspace.WorkspaceIcon.Image,
        "vault" => Helm.Modules.Vault.Ui.VaultIcon.Image,
        "keyboard" => Keyboard.Value,
        "window-zones" => WindowZones.Value,
        "slackbot" => SlackBot.Value,
        _ => null,
    };

    public IImage? For(string moduleId) => Of(moduleId);

    private static readonly Lazy<IImage> Keyboard = new(() =>
    {
        const double size = KeyboardIconShape.Size;
        var gradient = Gradient(size, KeyboardIconShape.GradientStart, KeyboardIconShape.GradientEnd);
        var group = Box(size);
        foreach (var (x, y, width, height) in KeyboardIconShape.Keys)
            group.Children.Add(new GeometryDrawing { Brush = gradient, Geometry = new RectangleGeometry(new Rect(x, y, width, height), KeyboardIconShape.Radius, KeyboardIconShape.Radius) });
        return new DrawingImage(group);
    });

    private static readonly Lazy<IImage> WindowZones = new(() =>
    {
        const double size = WindowZonesIconShape.Size;
        var gradient = Gradient(size, WindowZonesIconShape.GradientStart, WindowZonesIconShape.GradientEnd);
        var group = Box(size);
        group.Children.Add(new GeometryDrawing { Pen = new Pen(gradient, WindowZonesIconShape.Stroke, lineJoin: PenLineJoin.Round), Geometry = Geometry.Parse(WindowZonesIconShape.Zones) });
        return new DrawingImage(group);
    });

    private static readonly Lazy<IImage> SlackBot = new(() =>
    {
        static Geometry Rounded((double X, double Y, double Width, double Height, double Radius) b) =>
            new RectangleGeometry(new Rect(b.X, b.Y, b.Width, b.Height), b.Radius, b.Radius);
        const double size = SlackBotIconShape.Size;
        var head = Gradient(size, SlackBotIconShape.HeadStart, SlackBotIconShape.HeadEnd);
        var glow = new SolidColorBrush(Color.FromUInt32(SlackBotIconShape.GlowColor));
        var ears = new SolidColorBrush(Color.FromUInt32(SlackBotIconShape.EarColor));
        var group = Box(size);
        foreach (var ear in SlackBotIconShape.Ears) group.Children.Add(new GeometryDrawing { Brush = ears, Geometry = Rounded(ear) });
        group.Children.Add(new GeometryDrawing { Pen = new Pen(head, SlackBotIconShape.Stroke), Geometry = Rounded(SlackBotIconShape.Head) });
        var (sx, top, bottom) = SlackBotIconShape.Stalk;
        group.Children.Add(new GeometryDrawing { Pen = new Pen(glow, SlackBotIconShape.Stroke, lineCap: PenLineCap.Round), Geometry = new LineGeometry(new Point(sx, top), new Point(sx, bottom)) });
        var ball = SlackBotIconShape.Ball;
        group.Children.Add(new GeometryDrawing { Brush = glow, Geometry = new EllipseGeometry(new Rect(ball.X - ball.R, ball.Y - ball.R, ball.R * 2, ball.R * 2)) });
        foreach (var (ex, ey, er) in SlackBotIconShape.Eyes)
            group.Children.Add(new GeometryDrawing { Brush = glow, Geometry = new EllipseGeometry(new Rect(ex - er, ey - er, er * 2, er * 2)) });
        group.Children.Add(new GeometryDrawing { Brush = glow, Geometry = Rounded(SlackBotIconShape.Mouth) });
        return new DrawingImage(group);
    });

    private static LinearGradientBrush Gradient(double size, uint start, uint end) => new()
    {
        StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
        GradientStops = { new GradientStop(Color.FromUInt32(start), 0), new GradientStop(Color.FromUInt32(end), 1) },
    };

    // A transparent box keeps the icon's own margins at any size.
    private static DrawingGroup Box(double size) => new()
    {
        Children = { new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) } },
    };
}
