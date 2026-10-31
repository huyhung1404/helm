using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidColor = Android.Graphics.Color;
using AndroidView = Android.Views.View;

namespace Helm.Modules.Vocab;

/// <summary>
/// The card over the other apps (the pop-up and the bubble's), drawn with plain Android views (an Avalonia view in a
/// small overlay window grows into a larger opaque one): "Do you remember this word?" and the word; a Chinese word asks
/// for its pinyin first (the "Chinese: type the pinyin" setting), checked like on the Learn tab. Then its reading,
/// meaning and parts, and one row of icons: Listen, YouGlish, Full details (everything known about the word, so there
/// is no need to open the app), Open in Helm, Forgot, Remembered. Long content scrolls inside the card, never sideways.
/// The same card as the PC's pop-up (<see cref="VocabCardViewModel"/>); it closes once answered.
/// </summary>
internal sealed class VocabCardOverlay
{
    // Tone 1 red, 2 green, 3 blue, 4 purple, neutral grey: as everywhere in Helm (ToneText).
    private static readonly AndroidColor[] ToneColors =
        [AndroidColor.Rgb(0xE5, 0x39, 0x35), AndroidColor.Rgb(0x43, 0xA0, 0x47), AndroidColor.Rgb(0x1E, 0x88, 0xE5), AndroidColor.Rgb(0xAB, 0x47, 0xBC), AndroidColor.Rgb(0x9E, 0x9E, 0x9E)];

    private readonly Context _context;
    private readonly VocabCardViewModel _card;
    private readonly Action _close;
    private readonly Action<string> _open;
    private readonly Action<bool> _keyboard;
    private readonly Action<bool> _full;
    private readonly bool _dark;
    private readonly Palette _c;
    private readonly CardScroll _scroll;
    private readonly TextView _word;
    private readonly TextView _hint;
    private readonly LinearLayout _typeRow;
    private readonly EditText _typeBox;
    private readonly TextView _skip;
    private readonly LinearLayout _answer;
    private readonly TextView _pieces;
    private readonly TextView _verdict;
    private readonly TextView _reading;
    private readonly TextView _meaning;
    private readonly TextView _explanation;
    private readonly TextView _parts;
    private readonly LinearLayout _details;
    private readonly IconButton _listen, _youGlish, _fullButton, _forgot, _remembered;
    private bool _typing, _isFull;
    private string _detailsFor = "";

    /// <param name="keyboard">The pinyin box wants the keyboard (true) or is done with it (false).</param>
    /// <param name="full">Full details opened (true) or closed: the card grows to nearly the whole screen, or back.</param>
    public VocabCardOverlay(Context context, VocabCardViewModel card, Action close, Action<string> open, Action<bool> keyboard, Action<bool> full)
    {
        _context = context;
        _card = card;
        _close = close;
        _open = open;
        _keyboard = keyboard;
        _full = full;
        _dark = VocabOverlayPlatform.Dark(context);
        _c = new Palette(_dark);

        var content = new LinearLayout(context) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(16), Dp(10), Dp(10), Dp(12));

        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.AddView(Text("Do you remember this word?", 13, _c.Sub), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var closeButton = Text("✕", 18, _c.Sub);
        closeButton.ContentDescription = "Close";
        closeButton.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        closeButton.Click += (_, _) => _close();
        header.AddView(closeButton);
        content.AddView(header);

        _word = Text("", 28, _c.Ink, bold: true);
        _word.Click += (_, _) => WordTapped();
        content.AddView(_word);
        _hint = Text("Tap to see the meaning", 13, _c.Sub);
        _hint.Click += (_, _) => WordTapped();
        content.AddView(_hint);

        // The pinyin box: the card takes the keyboard only while it is used.
        _typeRow = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        _typeRow.SetPadding(0, Dp(6), Dp(6), 0);
        _typeBox = new EditText(context) { Hint = "Type its pinyin (tones optional)", ImeOptions = ImeAction.Done };
        _typeBox.SetSingleLine(true);
        // No suggestions or autocorrect for pinyin ("visible password" is the reliable way); that variation also picks
        // a monospace font, so the normal one is set back after it.
        _typeBox.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions | InputTypes.TextVariationVisiblePassword;
        _typeBox.Typeface = Typeface.Default;
        _typeBox.SetTextSize(Android.Util.ComplexUnitType.Sp, 16);
        _typeBox.SetTextColor(_c.Ink);
        _typeBox.SetHintTextColor(_c.Sub);
        _typeBox.Background = Round(_c.Field, 8, _c.Line);
        _typeBox.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        _typeBox.Touch += (_, e) =>
        {
            e.Handled = false;
            if (e.Event?.Action == MotionEventActions.Down) StartTyping();
        };
        _typeBox.TextChanged += (_, _) => _card.Typed = _typeBox.Text ?? "";
        _typeBox.EditorAction += (_, e) =>
        {
            e.Handled = true;
            Check();
        };
        _typeBox.KeyPress += (_, e) =>
        {
            e.Handled = false;
            if (e.KeyCode == Keycode.Back && e.Event?.Action == KeyEventActions.Up && _typing)
            {
                StopTyping();
                e.Handled = true;
            }
        };
        _typeRow.AddView(_typeBox, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var check = Button("Check", primary: true, Check);
        var checkParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        checkParams.SetMargins(Dp(8), 0, 0, 0);
        _typeRow.AddView(check, checkParams);
        content.AddView(_typeRow);
        _skip = Text("Show meaning", 13, _c.Accent);
        _skip.SetPadding(0, Dp(6), 0, Dp(2));
        _skip.Click += (_, _) => Reveal();
        content.AddView(_skip);

        _answer = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _pieces = Text("", 18, _c.Ink);
        _pieces.Typeface = Typeface.Monospace;
        _verdict = Text("", 14, _c.Ink, bold: true);
        _reading = Text("", 17, _c.Ink);
        _reading.SetPadding(0, Dp(2), 0, 0);
        _meaning = Text("", 17, _c.Ink);
        _meaning.SetPadding(0, Dp(4), 0, 0);
        _explanation = Text("", 14, _c.Sub);
        _parts = Text("", 13, _c.Sub);
        _parts.Typeface = Typeface.Monospace;
        _parts.SetPadding(0, Dp(6), 0, 0);
        _details = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        foreach (var view in new AndroidView[] { _pieces, _verdict, _reading, _meaning, _explanation, _parts, _details }) _answer.AddView(view);

        var icons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        icons.SetGravity(GravityFlags.CenterVertical);
        icons.SetPadding(0, Dp(10), Dp(6), 0);
        _listen = new IconButton(context, CardIcon.Listen, IconStyle.Plain, _c, "Listen", () => _card.SayCommand.Execute(null));
        _youGlish = new IconButton(context, CardIcon.Video, IconStyle.Plain, _c, "YouGlish", () => _card.YouGlishCommand.Execute(null));
        _fullButton = new IconButton(context, CardIcon.Expand, IconStyle.Plain, _c, "Full details", ToggleFull);
        var openButton = new IconButton(context, CardIcon.Open, IconStyle.Plain, _c, "Open in Helm", Open);
        _forgot = new IconButton(context, CardIcon.No, IconStyle.No, _c, "Forgot", () => _card.ForgotCommand.Execute(null));
        _remembered = new IconButton(context, CardIcon.Yes, IconStyle.Yes, _c, "Remembered", () => _card.RememberedCommand.Execute(null));
        foreach (var b in new AndroidView[] { _listen, _youGlish, _fullButton, openButton }) icons.AddView(b, IconParams());
        icons.AddView(new AndroidView(context), new LinearLayout.LayoutParams(0, 1, 1));
        icons.AddView(_forgot, IconParams());
        icons.AddView(_remembered, IconParams());
        _answer.AddView(icons);
        content.AddView(_answer);

        _scroll = new CardScroll(context) { Background = Round(_c.Bg, 14, _c.Line), Elevation = Dp(8) };
        _scroll.VerticalFadingEdgeEnabled = true;
        _scroll.SetFadingEdgeLength(Dp(28));
        _scroll.HorizontalScrollBarEnabled = false;
        _scroll.ClipToOutline = true;
        _scroll.AddView(content);
        View = _scroll;

        _card.PropertyChanged += OnCardChanged;
        _card.Answered += OnAnswered;
        Update();
    }

    public AndroidView View { get; }

    /// <summary>Being typed in or read: the card waits for an answer or ✕ instead of going away by itself.</summary>
    public bool IsBusy => _card.IsRevealed || _typing || _card.Typed.Length > 0;

    /// <summary>The most the card may grow before it scrolls (screen pixels).</summary>
    public int MaxHeight
    {
        get => _scroll.MaxHeight;
        set
        {
            _scroll.MaxHeight = value;
            _scroll.RequestLayout();
        }
    }

    /// <summary>The card left the screen: stop following the word, and give the keyboard back.</summary>
    public void Detach()
    {
        _card.PropertyChanged -= OnCardChanged;
        _card.Answered -= OnAnswered;
        HideKeyboard();
    }

    private void WordTapped()
    {
        if (_card.IsTyping)
        {
            StartTyping();
            return;
        }
        Reveal();
    }

    private void StartTyping()
    {
        if (_typing || !_card.IsTyping) return;
        _typing = true;
        _keyboard(true);
        _typeBox.Post(() =>
        {
            _typeBox.RequestFocus();
            (_context.GetSystemService(Context.InputMethodService) as InputMethodManager)?.ShowSoftInput(_typeBox, 0);
        });
    }

    private void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        HideKeyboard();
        _keyboard(false);
    }

    private void HideKeyboard() =>
        (_context.GetSystemService(Context.InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(_typeBox.WindowToken, HideSoftInputFlags.None);

    private void Check()
    {
        _card.Typed = _typeBox.Text ?? "";
        if (_card.Typed.Trim().Length == 0) return;
        StopTyping();
        _card.CheckCommand.Execute(null);
    }

    private void Reveal()
    {
        StopTyping();
        if (!_card.IsRevealed) _card.RevealCommand.Execute(null);
    }

    private void Open()
    {
        if (_card.WordId is { } id) _open(id);
        _close();
    }

    private void ToggleFull()
    {
        _isFull = !_isFull;
        _full(_isFull);
        Update();
        _scroll.ScrollTo(0, 0);
    }

    private void OnAnswered(object? sender, EventArgs e) => _close();

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        _word.Text = _card.Text;
        var revealed = _card.IsRevealed;
        var typing = _card.IsTyping;
        _hint.Visibility = !revealed && !typing ? ViewStates.Visible : ViewStates.Gone;
        _typeRow.Visibility = typing ? ViewStates.Visible : ViewStates.Gone;
        _skip.Visibility = typing ? ViewStates.Visible : ViewStates.Gone;
        _answer.Visibility = revealed ? ViewStates.Visible : ViewStates.Gone;
        if (!revealed) return;

        // What was typed, letter by letter, and the verdict.
        if (_card.HasAnswer)
        {
            var pieces = new SpannableStringBuilder();
            foreach (var piece in _card.AnswerPieces)
            {
                var start = pieces.Length();
                pieces.Append(piece.Text);
                if (piece.IsOk) continue;
                pieces.SetSpan(new ForegroundColorSpan(_c.Bad), start, pieces.Length(), SpanTypes.ExclusiveExclusive);
                if (piece.IsWrong) pieces.SetSpan(new UnderlineSpan(), start, pieces.Length(), SpanTypes.ExclusiveExclusive);
            }
            _pieces.TextFormatted = pieces;
            _verdict.Text = _card.AnswerMessage;
            _verdict.SetTextColor(_card.AnswerIsCorrect ? _c.Ok : _card.AnswerIsClose ? _c.Close : _c.Bad);
        }
        _pieces.Visibility = _verdict.Visibility = _card.HasAnswer ? ViewStates.Visible : ViewStates.Gone;

        _reading.TextFormatted = Reading();
        _reading.Visibility = _reading.Text?.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        Show(_meaning, _card.Meaning);
        Show(_explanation, _card.Explanation);
        Show(_parts, _isFull ? "" : string.Join("\n", _card.PartLines));
        _details.Visibility = _isFull ? ViewStates.Visible : ViewStates.Gone;
        if (_isFull) FillDetails();

        _listen.Visibility = _card.CanSay ? ViewStates.Visible : ViewStates.Gone;
        _youGlish.Visibility = _card.ShowYouGlish ? ViewStates.Visible : ViewStates.Gone;
        _fullButton.Set(_isFull ? CardIcon.Collapse : CardIcon.Expand, _isFull ? "Less" : "Full details");
        _remembered.Highlighted = _card.SuggestsRemembered;
        _forgot.Highlighted = _card.SuggestsForgot;
    }

    /// <summary>The pinyin in its tones' colours (or the IPA), then the Hán Việt and the traditional form.</summary>
    private SpannableStringBuilder Reading()
    {
        var text = new SpannableStringBuilder();
        foreach (var piece in PinyinTones.Split(_card.Phonetic))
        {
            var start = text.Length();
            text.Append(piece.Text);
            if (piece.Tone is >= 1 and <= 5) text.SetSpan(new ForegroundColorSpan(ToneColors[piece.Tone - 1]), start, text.Length(), SpanTypes.ExclusiveExclusive);
        }
        foreach (var extra in new[] { _card.HanViet, _card.Traditional }.Where(s => s.Length > 0))
        {
            text.Append("   ");
            var start = text.Length();
            text.Append(extra);
            text.SetSpan(new ForegroundColorSpan(_c.Sub), start, text.Length(), SpanTypes.ExclusiveExclusive);
            text.SetSpan(new StyleSpan(TypefaceStyle.Bold), start, text.Length(), SpanTypes.ExclusiveExclusive);
        }
        return text;
    }

    /// <summary>Everything Helm knows about the word (<see cref="VocabCardDetails"/>), built once per word.</summary>
    private void FillDetails()
    {
        var key = _card.WordId ?? "";
        if (_detailsFor == key) return;
        _detailsFor = key;
        _details.RemoveAllViews();
        if (_card.Info is { HasLabels: true } info)
        {
            var labels = Text(info.Labels, 12, _c.Ink);
            labels.Background = Round(_c.Btn, 10, null);
            labels.SetPadding(Dp(9), Dp(2), Dp(9), Dp(2));
            var p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            p.SetMargins(0, Dp(10), 0, 0);
            _details.AddView(labels, p);
        }
        foreach (var section in _card.Details())
        {
            var title = Text(section.Title.ToUpperInvariant(), 11, _c.Sub, bold: true);
            title.LetterSpacing = 0.08f;
            title.SetPadding(0, Dp(12), 0, Dp(4));
            _details.AddView(title);
            foreach (var item in section.Items)
            {
                var line = Text(section.IsTree ? item.Text : "• " + item.Text, section.IsTree ? 13 : 14, section.IsTree ? _c.Sub : _c.Ink, maxLines: 20);
                if (section.IsTree) line.Typeface = Typeface.Monospace;
                _details.AddView(line);
                if (!item.HasNote) continue;
                var note = Text(item.Note, 13, _c.Sub, maxLines: 20);
                note.SetPadding(Dp(10), 0, 0, Dp(2));
                _details.AddView(note);
            }
        }
    }

    private static void Show(TextView view, string text)
    {
        view.Text = text;
        view.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    private TextView Text(string text, float sp, AndroidColor color, bool bold = false, int maxLines = 8)
    {
        var view = new TextView(_context) { Text = text };
        view.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        view.SetTextColor(color);
        if (bold) view.SetTypeface(view.Typeface, TypefaceStyle.Bold);
        view.SetMaxLines(maxLines);
        view.Ellipsize = TextUtils.TruncateAt.End;
        return view;
    }

    private Button Button(string text, bool primary, Action click)
    {
        var button = new Button(_context) { Text = text, Background = Round(primary ? _c.Accent : _c.Btn, 8, null), StateListAnimator = null };
        button.SetAllCaps(false);
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        button.SetTextColor(primary ? _c.OnAccent : _c.Ink);
        button.SetMinHeight(Dp(44));
        button.SetMinimumHeight(Dp(44));
        button.SetPadding(Dp(14), 0, Dp(14), 0);
        button.Click += (_, _) => click();
        return button;
    }

    private GradientDrawable Round(AndroidColor fill, int radiusDp, AndroidColor? stroke)
    {
        var d = new GradientDrawable();
        d.SetColor(fill);
        d.SetCornerRadius(Dp(radiusDp));
        if (stroke is { } s) d.SetStroke(Dp(1), s);
        return d;
    }

    private LinearLayout.LayoutParams IconParams()
    {
        var p = new LinearLayout.LayoutParams(Dp(44), Dp(44));
        p.SetMargins(0, 0, Dp(6), 0);
        return p;
    }

    private int Dp(double dp) => VocabOverlayPlatform.Dp(_context, dp);

    /// <summary>The card's colours in the phone's light or dark theme.</summary>
    internal sealed class Palette(bool dark)
    {
        public AndroidColor Bg { get; } = dark ? AndroidColor.Rgb(0x2B, 0x2B, 0x2B) : AndroidColor.Rgb(0xFB, 0xFB, 0xFB);
        public AndroidColor Ink { get; } = dark ? AndroidColor.White : AndroidColor.Rgb(0x1A, 0x1A, 0x1A);
        public AndroidColor Sub { get; } = dark ? AndroidColor.Rgb(0xC5, 0xC5, 0xC5) : AndroidColor.Rgb(0x5F, 0x5F, 0x5F);
        public AndroidColor Line { get; } = dark ? AndroidColor.Rgb(0x3A, 0x3A, 0x3A) : AndroidColor.Rgb(0xE0, 0xE0, 0xE0);
        public AndroidColor Btn { get; } = dark ? AndroidColor.Rgb(0x3A, 0x3A, 0x3A) : AndroidColor.Rgb(0xEB, 0xEB, 0xEB);
        public AndroidColor Field { get; } = dark ? AndroidColor.Rgb(0x1F, 0x1F, 0x1F) : AndroidColor.White;
        public AndroidColor Accent { get; } = dark ? AndroidColor.Rgb(0x4C, 0xC2, 0xFF) : AndroidColor.Rgb(0x00, 0x67, 0xC0);
        public AndroidColor OnAccent { get; } = dark ? AndroidColor.Black : AndroidColor.White;
        public AndroidColor Ok { get; } = dark ? AndroidColor.Rgb(0x6C, 0xCB, 0x5F) : AndroidColor.Rgb(0x0F, 0x7B, 0x0F);
        public AndroidColor Close { get; } = dark ? AndroidColor.Rgb(0xE3, 0xB3, 0x41) : AndroidColor.Rgb(0x9A, 0x67, 0x00);
        public AndroidColor Bad { get; } = dark ? AndroidColor.Rgb(0xFF, 0x99, 0xA4) : AndroidColor.Rgb(0xC4, 0x2B, 0x1C);
    }

    /// <summary>A scroll view that grows with its content up to <see cref="MaxHeight"/>, then scrolls.</summary>
    private sealed class CardScroll(Context context) : ScrollView(context)
    {
        public int MaxHeight { get; set; } = int.MaxValue;

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            var size = MeasureSpec.GetSize(heightMeasureSpec);
            var mode = MeasureSpec.GetMode(heightMeasureSpec);
            var limit = mode == MeasureSpecMode.Unspecified ? MaxHeight : Math.Min(MaxHeight, size);
            base.OnMeasure(widthMeasureSpec, MeasureSpec.MakeMeasureSpec(limit, MeasureSpecMode.AtMost));
        }
    }
}

internal enum CardIcon
{
    Listen,
    Video,
    Expand,
    Collapse,
    Open,
    No,
    Yes,
}

internal enum IconStyle
{
    Plain,
    No,
    Yes,
}

/// <summary>A round 44 dp icon button, drawn on a 24-unit grid like the rest of Helm's line icons.</summary>
internal sealed class IconButton : AndroidView
{
    private readonly VocabCardOverlay.Palette _c;
    private readonly IconStyle _style;
    private CardIcon _icon;
    private bool _highlighted;

    public IconButton(Context context, CardIcon icon, IconStyle style, VocabCardOverlay.Palette palette, string name, Action click) : base(context)
    {
        _icon = icon;
        _style = style;
        _c = palette;
        ContentDescription = name;
        TooltipText = name;
        Clickable = true;
        Focusable = true;
        Click += (_, _) => click();
    }

    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value) return;
            _highlighted = value;
            Invalidate();
        }
    }

    public void Set(CardIcon icon, string name)
    {
        _icon = icon;
        ContentDescription = name;
        TooltipText = name;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var size = Math.Min(Width, Height);
        var center = size / 2f;
        var tint = _style switch
        {
            IconStyle.Yes => _c.Accent,
            IconStyle.No => _c.Bad,
            _ => _c.Sub,
        };
        if (_highlighted)
        {
            using var ring = new Paint(PaintFlags.AntiAlias) { Color = AndroidColor.Argb(0x73, tint.R, tint.G, tint.B) };
            canvas.DrawCircle(center, center, center, ring);
        }
        if (_style != IconStyle.Plain || Pressed)
        {
            var fill = _style switch
            {
                IconStyle.Yes => _c.Accent,
                IconStyle.No => AndroidColor.Argb(0x24, _c.Bad.R, _c.Bad.G, _c.Bad.B),
                _ => _c.Btn,
            };
            using var disc = new Paint(PaintFlags.AntiAlias) { Color = fill };
            canvas.DrawCircle(center, center, center - (_highlighted ? size * 0.07f : 0), disc);
        }
        var ink = _style == IconStyle.Yes ? _c.OnAccent : tint;
        var scale = size * 0.5f / 24f;
        canvas.Save();
        canvas.Translate(center - 12 * scale, center - 12 * scale);
        canvas.Scale(scale, scale);
        using var pen = new Paint(PaintFlags.AntiAlias) { Color = ink, StrokeWidth = 2, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
        pen.SetStyle(Paint.Style.Stroke);
        using var fillPen = new Paint(PaintFlags.AntiAlias) { Color = ink };
        switch (_icon)
        {
            case CardIcon.Listen:
                canvas.DrawPath(Poly(true, 4, 9.5f, 4, 14.5f, 7.5f, 14.5f, 12, 18.5f, 12, 5.5f, 7.5f, 9.5f), fillPen);
                canvas.DrawArc(new RectF(11.5f, 8.5f, 18.5f, 15.5f), -55, 110, false, pen);
                canvas.DrawArc(new RectF(9, 4.5f, 24, 19.5f), -50, 100, false, pen);
                break;
            case CardIcon.Video:
                canvas.DrawRoundRect(new RectF(3, 6.5f, 15, 17.5f), 2, 2, pen);
                canvas.DrawPath(Poly(true, 15, 10.5f, 21, 7.5f, 21, 16.5f, 15, 13.5f), pen);
                break;
            case CardIcon.Expand:
                canvas.DrawPath(Poly(false, 14, 4, 20, 4, 20, 10), pen);
                canvas.DrawLine(20, 4, 13, 11, pen);
                canvas.DrawPath(Poly(false, 10, 20, 4, 20, 4, 14), pen);
                canvas.DrawLine(4, 20, 11, 13, pen);
                break;
            case CardIcon.Collapse:
                canvas.DrawPath(Poly(false, 4, 14, 10, 14, 10, 20), pen);
                canvas.DrawLine(10, 14, 3, 21, pen);
                canvas.DrawPath(Poly(false, 20, 10, 14, 10, 14, 4), pen);
                canvas.DrawLine(14, 10, 21, 3, pen);
                break;
            case CardIcon.Open:
                canvas.DrawPath(Poly(false, 14, 4, 20, 4, 20, 10), pen);
                canvas.DrawLine(20, 4, 11, 13, pen);
                canvas.DrawPath(Poly(false, 18, 14, 18, 20, 4, 20, 4, 6, 10, 6), pen);
                break;
            case CardIcon.No:
                canvas.DrawLine(7, 7, 17, 17, pen);
                canvas.DrawLine(17, 7, 7, 17, pen);
                break;
            case CardIcon.Yes:
                canvas.DrawPath(Poly(false, 5, 12.5f, 9.5f, 17, 19, 7.5f), pen);
                break;
        }
        canvas.Restore();
    }

    protected override void DrawableStateChanged()
    {
        base.DrawableStateChanged();
        Invalidate();
    }

    private static Android.Graphics.Path Poly(bool closed, params float[] points)
    {
        var path = new Android.Graphics.Path();
        path.MoveTo(points[0], points[1]);
        for (var i = 2; i < points.Length; i += 2) path.LineTo(points[i], points[i + 1]);
        if (closed) path.Close();
        return path;
    }
}
