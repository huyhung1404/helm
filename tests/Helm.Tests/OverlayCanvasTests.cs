using Helm.Core.Geometry;
using Helm.Core.Interop;

namespace Helm.Tests;

/// <summary>The overlay canvas through the real GDI calls, on an off-screen bitmap (no window).</summary>
public class OverlayCanvasTests
{
    [Fact]
    public void Text_is_drawn()
    {
        // v0.51.0: the first label (an empty Window Zones zone) threw in CreateFont and ended the app.
        var painted = OverlayWindow.DrawOffScreen(240, 60, canvas =>
            canvas.Text(new PixelRect(0, 0, 240, 60), "Zone 1 is empty", new RgbColor(255, 255, 255), 20));
        Assert.InRange(painted, 50, 240 * 60 / 2);

        var regular = OverlayWindow.DrawOffScreen(240, 60, canvas =>
            canvas.Text(new PixelRect(0, 0, 240, 60), "2 windows", new RgbColor(255, 255, 255), 15, bold: false));
        Assert.True(regular > 0);
    }

    [Fact]
    public void Fill_and_frame_cover_what_they_should()
    {
        Assert.Equal(20 * 10, OverlayWindow.DrawOffScreen(40, 40, c => c.Fill(new PixelRect(5, 5, 25, 15), new RgbColor(43, 43, 43))));
        // A 2 px frame round a 20 × 20 square: 400 - 16 × 16.
        Assert.Equal(400 - 256, OverlayWindow.DrawOffScreen(40, 40, c => c.Frame(new PixelRect(0, 0, 20, 20), new RgbColor(255, 255, 255), 2)));
    }
}
