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
/// small overlay window grows into a larger opaque one). Its header: the icon, "Do you remember this word?", what the
/// card asks and ✕. Then the word, centred; a Chinese word asks for its pinyin first (the "Chinese: type the pinyin"
/// setting), checked like on the Learn tab, and says the verdict under it. Once shown, the answer (reading, meaning,
/// parts, the agent's way to remember the reading; Full details adds everything else known) scrolls between the word
/// and one fixed row of icons: Listen, YouGlish, Full details, Open in Helm, then ✗ and ✓. On a pinyin card those two
/// answer for the meaning only (a slipped pinyin is noted apart), which a caption above them says. The card is
/// see-through at rest (the "Card opacity" setting), so the app below stays readable, and solid while it is touched,
/// typed in or opened in full. The same card as the PC's pop-up (<see cref="VocabCardViewModel"/>); it closes once
/// answered.
/// </summary>
internal sealed partial class VocabCardOverlay
{
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
    private readonly CardFrame _frame;
    private readonly ScrollView _scroll;
    private readonly TextView _kind;
    private readonly LinearLayout _front;
    private readonly TextView _word;
    private readonly TextView _hint;
    private readonly LinearLayout _typeRow;
    private readonly EditText _typeBox;
    private readonly TextView _skip;
    private readonly TextView _verdict;
    private readonly TextView _verdictHint;
    private readonly LinearLayout _answer;
    private readonly LinearLayout _hook;
    private readonly TextView _hookText;
    private readonly TextView _reading;
    private readonly TextView _meaning;
    private readonly LinearLayout _parts;
    private readonly LinearLayout _details;
    private readonly LinearLayout _bar;
    private readonly TextView _answersCaption;
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

        // The header: the icon, the question, what the card asks, ✕.
        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(14), Dp(2), Dp(2), 0);
        header.AddView(new VocabLogo.View(context), new LinearLayout.LayoutParams(Dp(18), Dp(18)));
        var question = Text("Do you remember this word?", 13, _c.Sub);
        var questionParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
        questionParams.SetMargins(Dp(8), 0, Dp(8), 0);
        header.AddView(question, questionParams);
        _kind = Text("", 11, _c.AccentText, bold: true, maxLines: 1);
        _kind.Background = Round(_c.AccentFill, 4, null);
        _kind.SetPadding(Dp(7), Dp(2), Dp(7), Dp(2));
        header.AddView(_kind);
        header.AddView(new IconButton(context, CardIcon.Close, IconStyle.Plain, _c, "Close", () => _close(), iconDp: 18), new LinearLayout.LayoutParams(Dp(44), Dp(44)));

        // The front: the word, centred; a tap shows the answer, or on a pinyin card opens the box.
        _front = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _front.SetGravity(GravityFlags.CenterHorizontal);
        _front.Click += (_, _) => WordTapped();
        _word = Text("", 38, _c.Ink, bold: true, maxLines: 3);
        _word.Gravity = GravityFlags.CenterHorizontal;
        _front.AddView(_word);
        _hint = Text("Tap to see the meaning", 13, _c.Sub);
        _front.AddView(_hint, Gap(6));

        // The pinyin box: the card takes the keyboard only while it is used.
        _typeRow = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        _typeBox = new EditText(context) { Hint = "Type its pinyin (tones optional)", ImeOptions = ImeAction.Done };
        _typeBox.SetSingleLine(true);
        // No suggestions or autocorrect for pinyin ("visible password" is the reliable way); that variation also picks
        // a monospace font, so the normal one is set back after it.
        _typeBox.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions | InputTypes.TextVariationVisiblePassword;
        _typeBox.Typeface = Typeface.Default;
        _typeBox.SetTextSize(Android.Util.ComplexUnitType.Sp, 17);
        _typeBox.SetTextColor(_c.Ink);
        _typeBox.SetHintTextColor(_c.Sub);
        _typeBox.Background = Field();
        _typeBox.SetPadding(Dp(12), 0, Dp(12), 0);
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
        _typeRow.AddView(_typeBox, new LinearLayout.LayoutParams(0, Dp(44), 1));
        var check = Button("Check", Check);
        var checkParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(44));
        checkParams.SetMargins(Dp(8), 0, 0, 0);
        _typeRow.AddView(check, checkParams);
        _front.AddView(_typeRow, Gap(8, ViewGroup.LayoutParams.MatchParent));
        _skip = Text("Show meaning", 14, _c.Accent);
        _skip.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        _skip.Click += (_, _) => Reveal();
        _front.AddView(_skip, Gap(0));
        // The typed pinyin's verdict, and what the buttons now ask about.
        _verdict = Text("", 14, _c.Ink, bold: true);
        _verdict.Gravity = GravityFlags.CenterHorizontal;
        _front.AddView(_verdict, Gap(8));
        _verdictHint = Text("", 13, _c.Sub);
        _verdictHint.Gravity = GravityFlags.CenterHorizontal;
        _front.AddView(_verdictHint, Gap(2));

        // The answer, scrolling between the word and the icons.
        _answer = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _answer.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
        _reading = Text("", 18, _c.Ink);
        _answer.AddView(_reading);
        _meaning = Text("", 17, _c.Ink);
        _answer.AddView(_meaning, Gap(8));
        _parts = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _answer.AddView(_parts, Gap(8));
        // "Remembering the pinyin": the agent's way to remember the reading, in a purple box.
        _hook = new LinearLayout(context) { Orientation = Orientation.Vertical, Background = Round(_c.PyFill, 8, _c.PyStroke) };
        _hook.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        _hook.AddView(Text("Remembering the pinyin", 12, _c.Py, bold: true));
        _hookText = Text("", 14, _c.Ink, maxLines: 12);
        _hook.AddView(_hookText, Gap(2));
        _answer.AddView(_hook, Gap(8, ViewGroup.LayoutParams.MatchParent));
        _details = new LinearLayout(context) { Orientation = Orientation.Vertical, Background = TopLine() };
        _details.SetPadding(0, Dp(6), 0, 0);
        _answer.AddView(_details, Gap(8, ViewGroup.LayoutParams.MatchParent));
        _scroll = new ScrollView(context) { Background = TopLine(), VerticalFadingEdgeEnabled = true, HorizontalScrollBarEnabled = false };
        _scroll.SetFadingEdgeLength(Dp(24));
        _scroll.AddView(_answer);

        // The icons, fixed under the answer; on a pinyin card a caption says ✗ and ✓ are about the meaning.
        _bar = new LinearLayout(context) { Orientation = Orientation.Vertical, Background = TopLine() };
        _bar.SetPadding(Dp(8), Dp(4), Dp(8), Dp(8));
        _answersCaption = Text("", 12, _c.Sub);
        _answersCaption.Gravity = GravityFlags.End;
        _answersCaption.SetPadding(Dp(6), Dp(4), Dp(6), 0);
        var caption = new SpannableStringBuilder("✗ / ✓ answer for the ");
        var meaning = caption.Length();
        caption.Append("meaning");
        caption.SetSpan(new StyleSpan(TypefaceStyle.Bold), meaning, caption.Length(), SpanTypes.ExclusiveExclusive);
        caption.SetSpan(new ForegroundColorSpan(_c.Ink), meaning, caption.Length(), SpanTypes.ExclusiveExclusive);
        _answersCaption.TextFormatted = caption;
        _bar.AddView(_answersCaption);
        var icons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        icons.SetGravity(GravityFlags.CenterVertical);
        _listen = new IconButton(context, CardIcon.Listen, IconStyle.Plain, _c, "Listen", () => _card.SayCommand.Execute(null));
        _youGlish = new IconButton(context, CardIcon.Video, IconStyle.Plain, _c, "YouGlish", () => _card.YouGlishCommand.Execute(null));
        _fullButton = new IconButton(context, CardIcon.Expand, IconStyle.Plain, _c, "Full details", ToggleFull);
        var openButton = new IconButton(context, CardIcon.Open, IconStyle.Plain, _c, "Open in Helm", Open);
        _forgot = new IconButton(context, CardIcon.No, IconStyle.No, _c, "Forgot", () => _card.ForgotCommand.Execute(null));
        _remembered = new IconButton(context, CardIcon.Yes, IconStyle.Yes, _c, "Remembered", () => _card.RememberedCommand.Execute(null));
        foreach (var b in new AndroidView[] { _listen, _youGlish, _fullButton, openButton, _forgot }) icons.AddView(b, new LinearLayout.LayoutParams(Dp(44), Dp(44)));
        icons.AddView(new AndroidView(context), 4, new LinearLayout.LayoutParams(0, 1, 1));
        var yesParams = new LinearLayout.LayoutParams(Dp(44), Dp(44));
        yesParams.SetMargins(Dp(8), 0, 0, 0);
        icons.AddView(_remembered, yesParams);
        _bar.AddView(icons);

        _frame = new CardFrame(context, Touched) { Orientation = Orientation.Vertical, Background = Round(_c.Bg, 16, _c.Edge), Elevation = Dp(10) };
        _frame.ClipToOutline = true;
        _frame.AddView(header);
        _frame.AddView(_front);
        // Weighted, the answer gives way when the card reaches its height: the icons below always show.
        _frame.AddView(_scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 1));
        _frame.AddView(_bar);
        View = _frame;

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
        get => _frame.MaxHeight;
        set
        {
            _frame.MaxHeight = value;
            _frame.RequestLayout();
        }
    }

    /// <summary>The card left the screen: stop following the word, and give the keyboard back.</summary>
    public void Detach()
    {
        _frame.RemoveCallbacks(_settle);
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
        _frame.RemoveCallbacks(_settle);
        if (down) ApplyAlpha(animate: false);
        else _frame.PostDelayed(_settle, SettleMs);
    }

    /// <summary>Solid while used (touched, the pinyin box, Full details); the setting's opacity at rest.</summary>
    private void ApplyAlpha(bool animate)
    {
        var target = _touching || _typing || _isFull ? 1f : Math.Clamp(_restAlpha(), 0.4f, 1f);
        _frame.Animate()?.Cancel();
        if (animate) _frame.Animate()?.Alpha(target)?.SetDuration(FadeMs)?.Start();
        else _frame.Alpha = target;
    }

    private void OnAnswered(object? sender, EventArgs e) => _close();

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var revealed = _card.IsRevealed;
        var typing = _card.IsTyping;
        var pinyin = _card.AsksPinyin;
        _kind.Text = _card.KindText;
        _word.Text = _card.Text;
        // A pinyin card's word sits a little higher and larger, the box under it.
        _word.SetTextSize(Android.Util.ComplexUnitType.Sp, pinyin ? 42 : 38);
        _front.SetPadding(Dp(16), Dp(pinyin ? 6 : 12), Dp(16), Dp(pinyin ? 12 : 18));
        _front.Clickable = !revealed;
        _hint.Visibility = !revealed && !typing ? ViewStates.Visible : ViewStates.Gone;
        _typeRow.Visibility = _skip.Visibility = typing ? ViewStates.Visible : ViewStates.Gone;
        Show(_verdict, pinyin && _card.HasAnswer ? $"Pinyin “{_card.Typed}”: {_card.PinyinVerdictText}" : "");
        _verdict.SetTextColor(_card.AnswerIsCorrect ? _c.Ok : _card.AnswerIsClose ? _c.Close : _c.Bad);
        // The card's own, shorter word for what the two buttons now mean.
        Show(_verdictHint, _card.PinyinSlipped ? "Only the pinyin slipped? ✓ still counts: the buttons are about the meaning." : _card.VerdictHint);
        _scroll.Visibility = _bar.Visibility = revealed ? ViewStates.Visible : ViewStates.Gone;
        if (!revealed) return;

        _reading.TextFormatted = Reading();
        _reading.Visibility = _reading.Text?.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        Show(_meaning, _card.Meaning);
        _parts.RemoveAllViews();
        foreach (var line in _card.PartLines) _parts.AddView(Text(line, 14, _c.Parts), Gap(_parts.ChildCount == 0 ? 0 : 2));
        _parts.Visibility = _card.HasParts ? ViewStates.Visible : ViewStates.Gone;
        _hookText.Text = _card.PinyinMnemonic;
        _hook.Visibility = _card.HasPinyinMnemonic ? ViewStates.Visible : ViewStates.Gone;
        _details.Visibility = _isFull ? ViewStates.Visible : ViewStates.Gone;
        if (_isFull) FillDetails();
        _answersCaption.Visibility = pinyin ? ViewStates.Visible : ViewStates.Gone;

        _listen.Visibility = _card.CanSay ? ViewStates.Visible : ViewStates.Gone;
        _youGlish.Visibility = _card.ShowYouGlish ? ViewStates.Visible : ViewStates.Gone;
        _fullButton.Set(_isFull ? CardIcon.Collapse : CardIcon.Expand, _isFull ? "Less" : "Full details");
        _fullButton.On = _isFull;
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
            var tone = piece.Tone is >= 1 and <= 5 ? piece.Tone : piece.Text.Trim().Length > 0 ? 5 : 0;
            if (tone > 0) text.SetSpan(new ForegroundColorSpan(_c.Tone(tone)), start, text.Length(), SpanTypes.ExclusiveExclusive);
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
            var warn = section.Title.StartsWith("Easy to mix up", StringComparison.Ordinal);
            var title = Text(section.Title, 12, warn ? _c.Close : _c.Sub, bold: true);
            _details.AddView(title, Gap(10));
            foreach (var item in section.Items)
            {
                var line = Text(item.Text, section.IsTree ? 13 : 14, section.IsTree ? _c.Sub : _c.Ink, maxLines: 20);
                if (!item.HasNote)
                {
                    _details.AddView(line);
                    continue;
                }
                // A sentence and its translation, a bar beside them.
                var block = new LinearLayout(_context) { Orientation = Orientation.Vertical, Background = LeftBar() };
                block.SetPadding(Dp(8), 0, 0, 0);
                block.AddView(line);
                block.AddView(Text(item.Note, 13, _c.Sub, maxLines: 20));
                _details.AddView(block, Gap(2, ViewGroup.LayoutParams.MatchParent));
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

    private Button Button(string text, Action click)
    {
        var button = new Button(_context) { Text = text, Background = Round(_c.Accent, 6, null), StateListAnimator = null };
        button.SetAllCaps(false);
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
        button.SetTypeface(button.Typeface, TypefaceStyle.Bold);
        button.SetTextColor(_c.OnAccent);
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.SetPadding(Dp(16), 0, Dp(16), 0);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The pinyin box: dark, a faint edge and the accent along the bottom.</summary>
    private LayerDrawable Field()
    {
        var line = new GradientDrawable();
        line.SetColor(_c.Accent);
        var field = new LayerDrawable([Round(_c.Field, 6, _c.Line), line]);
        field.SetLayerHeight(1, Dp(2));
        field.SetLayerGravity(1, GravityFlags.Bottom);
        return field;
    }

    /// <summary>A hairline along the top (the answer, the icons, Full details).</summary>
    private LayerDrawable TopLine() => Edge(GravityFlags.Top);

    /// <summary>The bar beside an example sentence.</summary>
    private LayerDrawable LeftBar() => Edge(GravityFlags.Left, Dp(2), _c.Bar);

    private LayerDrawable Edge(GravityFlags side, int? thickness = null, AndroidColor? color = null)
    {
        var line = new GradientDrawable();
        line.SetColor(color ?? _c.Line);
        var edge = new LayerDrawable([line]);
        if (side == GravityFlags.Top) edge.SetLayerHeight(0, thickness ?? Math.Max(1, Dp(1)));
        else edge.SetLayerWidth(0, thickness ?? Math.Max(1, Dp(1)));
        edge.SetLayerGravity(0, side);
        return edge;
    }

    /// <summary>A child's layout with <paramref name="topDp"/> above it (the canvas's gaps).</summary>
    private LinearLayout.LayoutParams Gap(int topDp, int width = ViewGroup.LayoutParams.WrapContent)
    {
        var p = new LinearLayout.LayoutParams(width, ViewGroup.LayoutParams.WrapContent);
        p.SetMargins(0, Dp(topDp), 0, 0);
        return p;
    }

    private GradientDrawable Round(AndroidColor fill, int radiusDp, AndroidColor? stroke)
    {
        var d = new GradientDrawable();
        d.SetColor(fill);
        d.SetCornerRadius(Dp(radiusDp));
        if (stroke is { } s) d.SetStroke(Dp(1), s);
        return d;
    }

    private int Dp(double dp) => VocabOverlayPlatform.Dp(_context, dp);
}
