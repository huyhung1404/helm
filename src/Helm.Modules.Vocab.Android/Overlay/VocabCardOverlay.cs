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
/// is no need to open the app), Open in Helm, Forgot, Remembered. On a pinyin card those two answer for the meaning
/// only (a slipped pinyin is noted apart), which a caption above them says; the agent's way to remember the reading
/// shows with the answer. Long content scrolls inside the card, never sideways. The card is see-through at rest (the
/// "Card opacity" setting), so the app below stays readable, and solid while it is touched, typed in or opened in full.
/// The same card as the PC's pop-up (<see cref="VocabCardViewModel"/>); it closes once answered.
/// </summary>
internal sealed class VocabCardOverlay
{
    // Tone 1 red, 2 green, 3 blue, 4 purple, neutral grey: as everywhere in Helm (ToneText).
    private static readonly AndroidColor[] ToneColors =
        [AndroidColor.Rgb(0xE5, 0x39, 0x35), AndroidColor.Rgb(0x43, 0xA0, 0x47), AndroidColor.Rgb(0x1E, 0x88, 0xE5), AndroidColor.Rgb(0xAB, 0x47, 0xBC), AndroidColor.Rgb(0x9E, 0x9E, 0x9E)];

    // A tapped card stays solid this long after the finger lifts, then fades back over FadeMs.
    private const long SettleMs = 1200;
    private const long FadeMs = 220;

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
    private readonly TextView _verdictHint;
    private readonly LinearLayout _hook;
    private readonly TextView _hookText;
    private readonly TextView _answersCaption;
    private readonly TextView _reading;
    private readonly TextView _meaning;
    private readonly TextView _explanation;
    private readonly TextView _parts;
    private readonly LinearLayout _details;
    private readonly IconButton _listen, _youGlish, _fullButton, _forgot, _remembered;
    private readonly Func<float> _restAlpha;
    private readonly Action _settle;
    private bool _typing, _isFull, _touching;
    private string _detailsFor = "";

    /// <param name="keyboard">The pinyin box wants the keyboard (true) or is done with it (false).</param>
    /// <param name="full">Full details opened (true) or closed: the card grows to nearly the whole screen, or back.</param>
    /// <param name="restAlpha">How solid the card is while nobody uses it (0.4-1, the "Card opacity" setting).</param>
    public VocabCardOverlay(Context context, VocabCardViewModel card, Action close, Action<string> open, Action<bool> keyboard, Action<bool> full,
        Func<float> restAlpha)
    {
        _context = context;
        _card = card;
        _close = close;
        _open = open;
        _keyboard = keyboard;
        _full = full;
        _restAlpha = restAlpha;
        _settle = () => ApplyAlpha(animate: true);
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
        _verdictHint = Text("", 13, _c.Sub);
        _verdictHint.SetPadding(0, Dp(2), 0, Dp(2));
        _reading = Text("", 17, _c.Ink);
        _reading.SetPadding(0, Dp(2), 0, 0);
        _meaning = Text("", 17, _c.Ink);
        _meaning.SetPadding(0, Dp(4), 0, 0);
        _explanation = Text("", 14, _c.Sub);
        _parts = Text("", 13, _c.Sub);
        _parts.Typeface = Typeface.Monospace;
        _parts.SetPadding(0, Dp(6), 0, 0);
        // "Remembering the pinyin": the agent's way to remember the reading, in a purple box.
        _hook = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone, Background = Round(_c.PyFill, 8, _c.PyStroke) };
        _hook.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        _hook.AddView(Text("Remembering the pinyin", 12, _c.Py, bold: true));
        _hookText = Text("", 14, _c.Ink, maxLines: 12);
        _hook.AddView(_hookText);
        var hookParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        hookParams.SetMargins(0, Dp(8), Dp(6), 0);
        _details = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        foreach (var view in new AndroidView[] { _pieces, _verdict, _verdictHint, _reading, _meaning, _explanation, _parts }) _answer.AddView(view);
        _answer.AddView(_hook, hookParams);
        _answer.AddView(_details);

        // On a pinyin card: what the two answer buttons are about.
        _answersCaption = Text("", 12, _c.Sub);
        _answersCaption.Gravity = GravityFlags.End;
        _answersCaption.SetPadding(0, Dp(8), Dp(6), 0);
        var caption = new SpannableStringBuilder("✗ / ✓ answer for the ");
        var meaning = caption.Length();
        caption.Append("meaning");
        caption.SetSpan(new StyleSpan(TypefaceStyle.Bold), meaning, caption.Length(), SpanTypes.ExclusiveExclusive);
        caption.SetSpan(new ForegroundColorSpan(_c.Ink), meaning, caption.Length(), SpanTypes.ExclusiveExclusive);
        _answersCaption.TextFormatted = caption;
        _answer.AddView(_answersCaption);

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

        _scroll = new CardScroll(context, Touched) { Background = Round(_c.Bg, 14, _c.Line), Elevation = Dp(8) };
        _scroll.VerticalFadingEdgeEnabled = true;
        _scroll.SetFadingEdgeLength(Dp(28));
        _scroll.HorizontalScrollBarEnabled = false;
        _scroll.ClipToOutline = true;
        _scroll.AddView(content);
        View = _scroll;

        _card.PropertyChanged += OnCardChanged;
        _card.Answered += OnAnswered;
        Update();
        ApplyAlpha(animate: false);
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
        _scroll.RemoveCallbacks(_settle);
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
        ApplyAlpha(animate: false);
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
        ApplyAlpha(animate: true);
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
        ApplyAlpha(animate: !_isFull);
        _full(_isFull);
        Update();
        _scroll.ScrollTo(0, 0);
    }

    /// <summary>The "Card opacity" setting changed: the card at rest follows it at once.</summary>
    public void RestAlphaChanged() => ApplyAlpha(animate: true);

    /// <summary>A finger on the card (or lifted): solid while touched, back to see-through a moment after.</summary>
    private void Touched(bool down)
    {
        _touching = down;
        _scroll.RemoveCallbacks(_settle);
        if (down) ApplyAlpha(animate: false);
        else _scroll.PostDelayed(_settle, SettleMs);
    }

    /// <summary>Solid while used (touched, the pinyin box, Full details); the setting's opacity at rest.</summary>
    private void ApplyAlpha(bool animate)
    {
        var target = _touching || _typing || _isFull ? 1f : Math.Clamp(_restAlpha(), 0.4f, 1f);
        _scroll.Animate()?.Cancel();
        if (animate) _scroll.Animate()?.Alpha(target)?.SetDuration(FadeMs)?.Start();
        else _scroll.Alpha = target;
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
        Show(_verdictHint, _card.VerdictHint);

        _reading.TextFormatted = Reading();
        _reading.Visibility = _reading.Text?.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        Show(_meaning, _card.Meaning);
        Show(_explanation, _card.Explanation);
        Show(_parts, _isFull ? "" : string.Join("\n", _card.PartLines));
        // Full details has the hook as one of its sections.
        _hookText.Text = _card.PinyinMnemonic;
        _hook.Visibility = _card.HasPinyinMnemonic && !_isFull ? ViewStates.Visible : ViewStates.Gone;
        _details.Visibility = _isFull ? ViewStates.Visible : ViewStates.Gone;
        if (_isFull) FillDetails();
        _answersCaption.Visibility = _card.AsksPinyin ? ViewStates.Visible : ViewStates.Gone;

        _listen.Visibility = _card.CanSay ? ViewStates.Visible : ViewStates.Gone;
        _youGlish.Visibility = _card.ShowYouGlish ? ViewStates.Visible : ViewStates.Gone;
        _fullButton.Set(_isFull ? CardIcon.Collapse : CardIcon.Expand, _isFull ? "Less" : "Full details");
        _forgot.Set(CardIcon.No, _card.ForgotText);
        _remembered.Set(CardIcon.Yes, _card.RememberedText);
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
        // The pinyin hook's purple, as on the Learn tab.
        public AndroidColor Py { get; } = dark ? AndroidColor.Rgb(0xE7, 0xB5, 0xF2) : AndroidColor.Rgb(0x8A, 0x3F, 0xA8);
        public AndroidColor PyFill { get; } = dark ? AndroidColor.Argb(0x14, 0xD5, 0x8B, 0xEA) : AndroidColor.Argb(0x12, 0x8A, 0x3F, 0xA8);
        public AndroidColor PyStroke { get; } = dark ? AndroidColor.Argb(0x40, 0xD5, 0x8B, 0xEA) : AndroidColor.Argb(0x4D, 0x8A, 0x3F, 0xA8);
    }

    /// <summary>A scroll view that grows with its content up to <see cref="MaxHeight"/>, then scrolls; it tells when a finger is on it.</summary>
    private sealed class CardScroll(Context context, Action<bool> touched) : ScrollView(context)
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
