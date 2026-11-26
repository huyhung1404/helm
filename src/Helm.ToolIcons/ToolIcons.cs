using Avalonia;
using Avalonia.Media;
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
        "missions" => Helm.Modules.Missions.Ui.MissionsIcon.Image,
        "notes" => Helm.Modules.Notes.NotesIcon.Image,
        "novel-reader" => Helm.Modules.NovelReader.Ui.NovelReaderIcon.Image,
        "quick-capture" => Helm.Modules.QuickCapture.QuickCaptureIcon.Image,
        "scratch" => Helm.Modules.Scratch.ScratchIcon.Image,
        "ssh" => Helm.Modules.Ssh.Ui.SshIcon.Image,
        "tracker" => Helm.Modules.Tracker.TrackerIcon.Image,
        "vocab" => Helm.Modules.Vocab.Ui.VocabIcon.Image,
        "wallet" => Helm.Modules.Wallet.Ui.WalletIcon.Image,
        "vault" => Helm.Modules.Vault.Ui.VaultIcon.Image,
        "keyboard" => Helm.Modules.Keyboard.Ui.KeyboardIcon.Image,
        "window-zones" => WindowZones.Value,
        "slackbot" => Helm.Modules.SlackBot.Ui.SlackBotIcon.Image,
        _ => null,
    };

    public IImage? For(string moduleId) => Of(moduleId);


    private static readonly Lazy<IImage> WindowZones = new(() =>
    {
        const double size = WindowZonesIconShape.Size;
        var gradient = Gradient(size, WindowZonesIconShape.GradientStart, WindowZonesIconShape.GradientEnd);
        var group = Box(size);
        group.Children.Add(new GeometryDrawing { Pen = new Pen(gradient, WindowZonesIconShape.Stroke, lineJoin: PenLineJoin.Round), Geometry = Geometry.Parse(WindowZonesIconShape.Zones) });
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
