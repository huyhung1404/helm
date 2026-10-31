using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidColor = Android.Graphics.Color;
using AndroidView = Android.Views.View;

namespace Helm.Modules.Vocab;

/// <summary>
/// The pop-up card over the other apps, drawn with plain Android views (an Avalonia view in a small overlay window grows
/// into a larger opaque one): "Do you remember this word?", the word, and after a tap its reading, meaning, explanation and
/// parts, with Listen, Open in Helm, Forgot and Remembered. The same card as the PC's pop-up
/// (<see cref="VocabCardViewModel"/>); it closes once answered.
/// </summary>
internal sealed class VocabCardOverlay
{
    private readonly Context _context;
    private readonly VocabCardViewModel _card;
    private readonly Action _close;
    private readonly Action<string> _open;
    private readonly bool _dark;
    private readonly TextView _word;
    private readonly TextView _hint;
    private readonly LinearLayout _answer;
    private readonly TextView _reading;
    private readonly TextView _meaning;
    private readonly TextView _explanation;
    private readonly TextView _parts;
    private readonly Button _listen;

    public VocabCardOverlay(Context context, VocabCardViewModel card, Action close, Action<string> open)
    {
        _context = context;
        _card = card;
        _close = close;
        _open = open;
        _dark = VocabOverlayPlatform.Dark(context);

        var root = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(_dark ? AndroidColor.Rgb(0x2B, 0x2B, 0x2B) : AndroidColor.Rgb(0xFB, 0xFB, 0xFB));
        background.SetCornerRadius(Dp(14));
        background.SetStroke(Dp(1), _dark ? AndroidColor.Rgb(0x3A, 0x3A, 0x3A) : AndroidColor.Rgb(0xE0, 0xE0, 0xE0));
        root.Background = background;
        root.Elevation = Dp(8);
        root.SetPadding(Dp(16), Dp(10), Dp(10), Dp(14));

        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var title = Text("Do you remember this word?", 13, secondary: true);
        header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var closeButton = Text("✕", 18, secondary: true);
        closeButton.ContentDescription = "Close";
        closeButton.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        closeButton.Click += (_, _) => _close();
        header.AddView(closeButton);
        root.AddView(header);

        _word = Text("", 26, bold: true);
        _word.SetPadding(0, Dp(2), 0, 0);
        _word.Click += (_, _) => Reveal();
        root.AddView(_word);
        _hint = Text("Tap to see the meaning", 13, secondary: true);
        _hint.Click += (_, _) => Reveal();
        root.AddView(_hint);

        _answer = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _reading = Text("", 15, secondary: true);
        _meaning = Text("", 17);
        _meaning.SetPadding(0, Dp(6), 0, 0);
        _explanation = Text("", 14, secondary: true);
        _explanation.SetPadding(0, Dp(4), 0, 0);
        _parts = Text("", 13, secondary: true);
        _parts.SetPadding(0, Dp(6), 0, 0);
        _parts.Typeface = Typeface.Monospace;
        _answer.AddView(_reading);
        _answer.AddView(_meaning);
        _answer.AddView(_explanation);
        _answer.AddView(_parts);

        var tools = Row();
        _listen = Button("🔊 Listen", primary: false, () => _card.SayCommand.Execute(null));
        tools.AddView(_listen, Weighted());
        tools.AddView(Button("Open in Helm", primary: false, Open), Weighted());
        _answer.AddView(tools);
        var verdict = Row();
        verdict.AddView(Button("✗ Forgot", primary: false, () => _card.ForgotCommand.Execute(null)), Weighted());
        verdict.AddView(Button("✓ Remembered", primary: true, () => _card.RememberedCommand.Execute(null)), Weighted());
        _answer.AddView(verdict);
        root.AddView(_answer);

        View = root;
        _card.PropertyChanged += OnCardChanged;
        _card.Answered += OnAnswered;
        Update();
    }

    public AndroidView View { get; }

    /// <summary>The meaning is showing: the card stays until it is answered or closed.</summary>
    public bool IsRevealed => _card.IsRevealed;

    /// <summary>The card left the screen: stop following the word.</summary>
    public void Detach()
    {
        _card.PropertyChanged -= OnCardChanged;
        _card.Answered -= OnAnswered;
    }

    private void Reveal()
    {
        if (!_card.IsRevealed) _card.RevealCommand.Execute(null);
    }

    private void Open()
    {
        if (_card.WordId is { } id) _open(id);
        _close();
    }

    private void OnAnswered(object? sender, EventArgs e) => _close();

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        _word.Text = _card.Text;
        var revealed = _card.IsRevealed;
        _hint.Visibility = revealed ? ViewStates.Gone : ViewStates.Visible;
        _answer.Visibility = revealed ? ViewStates.Visible : ViewStates.Gone;
        if (!revealed) return;
        var reading = string.Join("   ", new[] { _card.Phonetic, _card.HanViet, _card.Traditional }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Show(_reading, reading);
        Show(_meaning, _card.Meaning);
        Show(_explanation, _card.Explanation);
        Show(_parts, string.Join("\n", _card.PartLines));
        _listen.Visibility = _card.CanSay ? ViewStates.Visible : ViewStates.Gone;
    }

    private static void Show(TextView view, string text)
    {
        view.Text = text;
        view.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    private TextView Text(string text, float sp, bool secondary = false, bool bold = false)
    {
        var view = new TextView(_context) { Text = text };
        view.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        view.SetTextColor(secondary
            ? (_dark ? AndroidColor.Rgb(0xC5, 0xC5, 0xC5) : AndroidColor.Rgb(0x5F, 0x5F, 0x5F))
            : (_dark ? AndroidColor.White : AndroidColor.Rgb(0x1A, 0x1A, 0x1A)));
        if (bold) view.SetTypeface(view.Typeface, TypefaceStyle.Bold);
        view.Ellipsize = TextUtils.TruncateAt.End;
        view.SetMaxLines(sp >= 20 ? 2 : 6);
        return view;
    }

    private LinearLayout Row()
    {
        var row = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Dp(10), 0, 0);
        return row;
    }

    private LinearLayout.LayoutParams Weighted()
    {
        var p = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
        p.SetMargins(Dp(3), 0, Dp(3), 0);
        return p;
    }

    private Button Button(string text, bool primary, Action click)
    {
        var button = new Button(_context) { Text = text };
        button.SetAllCaps(false);
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        var background = new GradientDrawable();
        background.SetCornerRadius(Dp(8));
        var accent = _dark ? AndroidColor.Rgb(0x4C, 0xC2, 0xFF) : AndroidColor.Rgb(0x00, 0x67, 0xC0);
        background.SetColor(primary ? accent : (_dark ? AndroidColor.Rgb(0x3A, 0x3A, 0x3A) : AndroidColor.Rgb(0xEB, 0xEB, 0xEB)));
        button.Background = background;
        button.SetTextColor(primary ? (_dark ? AndroidColor.Black : AndroidColor.White) : (_dark ? AndroidColor.White : AndroidColor.Rgb(0x1A, 0x1A, 0x1A)));
        button.SetMinHeight(Dp(44));
        button.SetMinimumHeight(Dp(44));
        button.StateListAnimator = null;
        button.Click += (_, _) => click();
        return button;
    }

    private int Dp(double dp) => VocabOverlayPlatform.Dp(_context, dp);
}
