using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidColor = Android.Graphics.Color;

namespace Helm.Modules.Vocab;

// The card's colours and its frame (VocabCardOverlay builds the card from them).
internal sealed partial class VocabCardOverlay
{
    /// <summary>The card's colours in the phone's light or dark theme.</summary>
    internal sealed class Palette(bool dark)
    {
        public AndroidColor Bg { get; } = dark ? AndroidColor.Rgb(0x20, 0x20, 0x20) : AndroidColor.Rgb(0xFB, 0xFB, 0xFB);
        public AndroidColor Edge { get; } = dark ? AndroidColor.Argb(0x1A, 0xFF, 0xFF, 0xFF) : AndroidColor.Argb(0x1A, 0, 0, 0);
        public AndroidColor Ink { get; } = dark ? AndroidColor.White : AndroidColor.Rgb(0x1A, 0x1A, 0x1A);
        public AndroidColor Sub { get; } = dark ? AndroidColor.Rgb(0xC5, 0xC5, 0xC5) : AndroidColor.Rgb(0x5F, 0x5F, 0x5F);
        public AndroidColor Parts { get; } = dark ? AndroidColor.Rgb(0xD6, 0xD6, 0xD6) : AndroidColor.Rgb(0x3A, 0x3A, 0x3A);
        public AndroidColor Line { get; } = dark ? AndroidColor.Argb(0x12, 0xFF, 0xFF, 0xFF) : AndroidColor.Argb(0x14, 0, 0, 0);
        public AndroidColor Bar { get; } = dark ? AndroidColor.Argb(0x1F, 0xFF, 0xFF, 0xFF) : AndroidColor.Argb(0x24, 0, 0, 0);
        public AndroidColor Btn { get; } = dark ? AndroidColor.Rgb(0x3A, 0x3A, 0x3A) : AndroidColor.Rgb(0xEB, 0xEB, 0xEB);
        public AndroidColor Field { get; } = dark ? AndroidColor.Rgb(0x1A, 0x1A, 0x1A) : AndroidColor.White;
        public AndroidColor Accent { get; } = dark ? AndroidColor.Rgb(0x60, 0xCD, 0xFF) : AndroidColor.Rgb(0x00, 0x67, 0xC0);
        public AndroidColor OnAccent { get; } = dark ? AndroidColor.Black : AndroidColor.White;
        // .rb.on (Full details open): the accent at .16; .tag: at .14.
        public AndroidColor AccentFill { get; } = dark ? AndroidColor.Argb(0x29, 0x60, 0xCD, 0xFF) : AndroidColor.Argb(0x24, 0x00, 0x67, 0xC0);
        public AndroidColor TagFill { get; } = dark ? AndroidColor.Argb(0x24, 0x60, 0xCD, 0xFF) : AndroidColor.Argb(0x1F, 0x00, 0x67, 0xC0);
        public AndroidColor AccentText { get; } = dark ? AndroidColor.Rgb(0x8A, 0xD9, 0xFF) : AndroidColor.Rgb(0x00, 0x5A, 0x9E);
        // "To remember it" (.memo): the accent at .08, an edge at .3.
        public AndroidColor MemoFill { get; } = dark ? AndroidColor.Argb(0x14, 0x60, 0xCD, 0xFF) : AndroidColor.Argb(0x0F, 0x00, 0x67, 0xC0);
        public AndroidColor MemoStroke { get; } = dark ? AndroidColor.Argb(0x4D, 0x60, 0xCD, 0xFF) : AndroidColor.Argb(0x4D, 0x00, 0x67, 0xC0);
        public AndroidColor Ok { get; } = dark ? AndroidColor.Rgb(0x6F, 0xD0, 0x8C) : AndroidColor.Rgb(0x0F, 0x7B, 0x0F);
        public AndroidColor OnOk { get; } = dark ? AndroidColor.Rgb(0x0B, 0x2A, 0x14) : AndroidColor.White;
        public AndroidColor Close { get; } = dark ? AndroidColor.Rgb(0xFF, 0xC9, 0x83) : AndroidColor.Rgb(0x9A, 0x67, 0x00);
        public AndroidColor Bad { get; } = dark ? AndroidColor.Rgb(0xFF, 0x99, 0xA4) : AndroidColor.Rgb(0xC4, 0x2B, 0x1C);
        // The pinyin hook's purple, as on the Learn tab.
        public AndroidColor Py { get; } = dark ? AndroidColor.Rgb(0xE7, 0xB5, 0xF2) : AndroidColor.Rgb(0x8A, 0x3F, 0xA8);
        public AndroidColor PyFill { get; } = dark ? AndroidColor.Argb(0x14, 0xD5, 0x8B, 0xEA) : AndroidColor.Argb(0x12, 0x8A, 0x3F, 0xA8);
        public AndroidColor PyStroke { get; } = dark ? AndroidColor.Argb(0x40, 0xD5, 0x8B, 0xEA) : AndroidColor.Argb(0x4D, 0x8A, 0x3F, 0xA8);

        // Tone 1 red, 2 green, 3 blue, 4 purple, neutral grey: as on the Learn tab (ToneText), lighter in the dark.
        private readonly AndroidColor[] _tones = dark
            ? [AndroidColor.Rgb(0xFF, 0x7A, 0x7A), AndroidColor.Rgb(0x6F, 0xD0, 0x8C), AndroidColor.Rgb(0x5C, 0xB3, 0xFF), AndroidColor.Rgb(0xD5, 0x8B, 0xEA), AndroidColor.Rgb(0xC5, 0xC5, 0xC5)]
            : [AndroidColor.Rgb(0xE5, 0x39, 0x35), AndroidColor.Rgb(0x43, 0xA0, 0x47), AndroidColor.Rgb(0x1E, 0x88, 0xE5), AndroidColor.Rgb(0xAB, 0x47, 0xBC), AndroidColor.Rgb(0x5F, 0x5F, 0x5F)];

        public AndroidColor Tone(int tone) => _tones[tone - 1];
    }

    /// <summary>
    /// The card: a column that grows with its content up to <see cref="MaxHeight"/> (its weighted answer then scrolls);
    /// it tells when a finger is on it.
    /// </summary>
    private sealed class CardFrame(Context context, Action<bool> touched) : LinearLayout(context)
    {
        public int MaxHeight { get; set; } = int.MaxValue;

        // Seen before any child handles the touch (a button, the pinyin box, scrolling).
        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            switch (e?.ActionMasked)
            {
                case MotionEventActions.Down:
                    touched(true);
                    break;
                case MotionEventActions.Up or MotionEventActions.Cancel:
                    touched(false);
                    break;
            }
            return base.DispatchTouchEvent(e);
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            var size = MeasureSpec.GetSize(heightMeasureSpec);
            var mode = MeasureSpec.GetMode(heightMeasureSpec);
            var limit = mode == MeasureSpecMode.Unspecified ? MaxHeight : Math.Min(MaxHeight, size);
            base.OnMeasure(widthMeasureSpec, MeasureSpec.MakeMeasureSpec(limit, MeasureSpecMode.AtMost));
        }
    }
}
