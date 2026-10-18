using Android.Gms.Tasks;
using Helm.Modules.NovelReader.Lens;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Chinese;
using Bitmap = Android.Graphics.Bitmap;
using RecognisedText = Xamarin.Google.MLKit.Vision.Text.Text;
using Task = System.Threading.Tasks.Task;

namespace Helm.Modules.NovelReader.Lens;

/// <summary>
/// Chinese text recognition on the phone (ML Kit, its model inside the APK: offline, no Play services). Gives every
/// line recognised with its box in the screenshot's pixels; <see cref="LensPageReader"/> makes the page of them.
/// </summary>
internal sealed class LensOcr : IDisposable
{
    /// <summary>
    /// The recognizer scales an image down by its longer side and then mistakes similar characters (measured on a
    /// 2560×1440 tablet screen: about one character in twenty wrong; cropped to the text, almost none). So only the
    /// text's columns are read, in bands top to bottom that overlap; a line is taken from the band whose own part (the
    /// band without half of each overlap) holds its middle, so a line cut by one band is whole in the next.
    /// </summary>
    private const int Bands = 3;

    private const double Overlap = 0.08;

    private readonly ITextRecognizer _recognizer = TextRecognition.GetClient(new ChineseTextRecognizerOptions.Builder().Build());

    public async Task<IReadOnlyList<OcrLine>> ReadAsync(Bitmap screenshot)
    {
        var (left, right) = TextColumns(screenshot);
        var lines = new List<OcrLine>();
        var height = screenshot.Height;
        var overlap = (int)(height * Overlap);
        var step = (double)height / Bands;
        for (var band = 0; band < Bands; band++)
        {
            var ownTop = (int)(band * step);
            var ownBottom = band == Bands - 1 ? height : (int)((band + 1) * step);
            var top = Math.Max(0, ownTop - overlap);
            var bottom = Math.Min(height, ownBottom + overlap);
            using var part = Bitmap.CreateBitmap(screenshot, left, top, right - left, bottom - top)!;
            foreach (var line in await RecogniseAsync(part).ConfigureAwait(true))
            {
                var placed = new OcrLine(line.Text, line.Left + left, line.Top + top, line.Right + left, line.Bottom + top);
                if (placed.Middle >= ownTop && placed.Middle < ownBottom) lines.Add(placed);
            }
        }
        return lines;
    }

    /// <summary>
    /// The columns that hold the page's text, with a margin: where a small grey copy of the screenshot differs clearly
    /// from its background, looking at the middle of the screen only (the status bar's icons and the app's footer
    /// reach further right than the text). The whole width when nothing stands out.
    /// </summary>
    private static (int Left, int Right) TextColumns(Bitmap shot)
    {
        const int Scale = 8;
        var w = Math.Max(1, shot.Width / Scale);
        var h = Math.Max(1, shot.Height / Scale);
        using var small = Bitmap.CreateScaledBitmap(shot, w, h, true)!;
        var pixels = new int[w * h];
        small.GetPixels(pixels, 0, w, 0, 0, w, h);
        var grey = new int[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            grey[i] = (((p >> 16) & 0xFF) * 3 + ((p >> 8) & 0xFF) * 6 + (p & 0xFF)) / 10;
        }
        var background = grey.Order().ElementAt(grey.Length / 2);
        int first = w, last = -1;
        for (var y = h * 12 / 100; y < h * 88 / 100; y++)
            for (var x = 0; x < w; x++)
                if (Math.Abs(grey[y * w + x] - background) > 70)
                {
                    first = Math.Min(first, x);
                    last = Math.Max(last, x);
                }
        if (last < first) return (0, shot.Width);
        var margin = shot.Width / 40;
        var left = Math.Max(0, first * Scale - margin);
        var right = Math.Min(shot.Width, (last + 1) * Scale + margin);
        return right - left < shot.Width / 4 ? (0, shot.Width) : (left, right);
    }

    private async Task<List<OcrLine>> RecogniseAsync(Bitmap image)
    {
        var listener = new Listener();
        _recognizer.Process(InputImage.FromBitmap(image, 0)).AddOnSuccessListener(listener).AddOnFailureListener(listener);
        var text = (RecognisedText)await listener.Done.Task.ConfigureAwait(true);
        var lines = new List<OcrLine>();
        foreach (var block in text.TextBlocks)
            foreach (var line in block.Lines)
                if (line.BoundingBox is { } box)
                    lines.Add(new OcrLine(line.Text, box.Left, box.Top, box.Right, box.Bottom));
        return lines;
    }

    public void Dispose() => _recognizer.Close();

    private sealed class Listener : Java.Lang.Object, IOnSuccessListener, IOnFailureListener
    {
        public TaskCompletionSource<Java.Lang.Object> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnSuccess(Java.Lang.Object? result)
        {
            if (result is null) Done.TrySetException(new InvalidOperationException("Text recognition gave nothing"));
            else Done.TrySetResult(result);
        }

        public void OnFailure(Java.Lang.Exception e) => Done.TrySetException(e);
    }
}
