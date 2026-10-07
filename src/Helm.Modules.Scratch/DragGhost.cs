using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helm.Modules.Scratch;

/// <summary>
/// A picture of the card being dragged that follows the cursor over the page, so you see what you move. Drawn on the
/// page's adorner layer; it never takes the mouse, so the card under the cursor still gets the drop.
/// </summary>
internal sealed class DragGhost : Adorner
{
    private readonly ImageBrush _picture;
    private readonly Size _size;
    private readonly Vector _grab;
    private Point _cursor;

    /// <param name="layerOwner">The element whose adorner layer shows the ghost (it covers the page).</param>
    /// <param name="card">The card being dragged.</param>
    /// <param name="grab">Where in the card it was picked up, so the card keeps that point under the cursor.</param>
    public DragGhost(UIElement layerOwner, FrameworkElement card, Point grab) : base(layerOwner)
    {
        _size = new Size(card.ActualWidth, card.ActualHeight);
        // A snapshot taken now, before the card's place is faded.
        var dpi = VisualTreeHelper.GetDpi(card);
        var snapshot = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(_size.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(_size.Height * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Through a VisualBrush: rendering the card itself would shift it by its place in the wall.
        var frame = new DrawingVisual();
        using (var context = frame.RenderOpen())
            context.DrawRectangle(new VisualBrush(card) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(_size));
        snapshot.Render(frame);
        snapshot.Freeze();
        Snapshot = snapshot;
        _picture = new ImageBrush(snapshot);
        _grab = (Vector)grab;
        IsHitTestVisible = false;
    }

    /// <summary>The picture of the card (tests check it is the card, not shifted).</summary>
    internal BitmapSource Snapshot { get; }

    /// <summary>Moves the ghost to the cursor (a point of the layer's owner).</summary>
    public void MoveTo(Point cursor)
    {
        _cursor = cursor;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var at = _cursor - _grab;
        var bounds = new Rect(at, _size);
        drawing.PushOpacity(0.85);
        // A soft shadow lifts it off the page.
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Rect(at + new Vector(3, 5), _size), 8, 8);
        drawing.DrawRoundedRectangle(_picture, null, bounds, 8, 8);
        drawing.Pop();
    }
}
