using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>
/// A paragraph in study mode: one block per word, wrapping like text, with the word's Chinese, pinyin, Hán Việt and
/// Vietnamese on four centred rows (each row can be hidden). Punctuation joins the Vietnamese of the word before it. A
/// tap opens the word and a double tap reads aloud from there, through <see cref="SegmentText"/>'s events; a long press
/// holds the word (<see cref="WordHeldEvent"/>: Novel Reader explains its sentence) and opens nothing when the finger
/// lifts. The sentence being read (or held) is highlighted; under a mouse the paragraph is marked for reading by eye like
/// <see cref="SegmentText"/>.
/// </summary>
public sealed class StudyText : WrapPanel
{
    public static readonly StyledProperty<ConvertedLine?> LineProperty = AvaloniaProperty.Register<StudyText, ConvertedLine?>(nameof(Line));

    public static readonly StyledProperty<IReadOnlyList<WordReading?>?> ReadingsProperty =
        AvaloniaProperty.Register<StudyText, IReadOnlyList<WordReading?>?>(nameof(Readings));

    public static readonly StyledProperty<IReadOnlyList<Sentence>?> SentencesProperty =
        AvaloniaProperty.Register<StudyText, IReadOnlyList<Sentence>?>(nameof(Sentences));

    public static readonly StyledProperty<double> TextSizeProperty = AvaloniaProperty.Register<StudyText, double>(nameof(TextSize), 18);

    public static readonly StyledProperty<bool> ShowChineseProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(ShowChinese), true);

    public static readonly StyledProperty<bool> ShowPinyinProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(ShowPinyin), true);

    public static readonly StyledProperty<bool> ShowHanVietProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(ShowHanViet), true);

    public static readonly StyledProperty<bool> HoverHighlightProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(HoverHighlight));

    public static readonly StyledProperty<bool> GrammarColorsProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(GrammarColors));

    /// <summary>The segments of the sentence being read, as (first, count); null highlights nothing.</summary>
    public static readonly StyledProperty<(int First, int Count)?> HighlightProperty =
        AvaloniaProperty.Register<StudyText, (int First, int Count)?>(nameof(Highlight));

    /// <summary>A word was pressed long (here or on <see cref="SegmentText"/>).</summary>
    public static readonly RoutedEvent<WordHeldEventArgs> WordHeldEvent =
        RoutedEvent.Register<StudyText, WordHeldEventArgs>(nameof(WordHeld), RoutingStrategies.Bubble);

    private static readonly TimeSpan DoubleTapWait = TimeSpan.FromMilliseconds(230);

    /// <summary>A finger kept down this long on a word holds it; moving further than <see cref="HoldSlop"/> is a scroll.</summary>
    internal static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(450);

    internal const double HoldSlop = 10;

    private readonly List<(TextBlock Block, Run Vietnamese, int Index, Segment Segment, string Chinese)> _words = [];
    private IDisposable? _tapWait;
    private IDisposable? _holdWait;
    private Point _pressedAt;
    private bool _held;

    public StudyText() => ActualThemeVariantChanged += (_, _) => Rebuild();

    public event EventHandler<WordHeldEventArgs>? WordHeld
    {
        add => AddHandler(WordHeldEvent, value);
        remove => RemoveHandler(WordHeldEvent, value);
    }

    public ConvertedLine? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }

    public IReadOnlyList<WordReading?>? Readings { get => GetValue(ReadingsProperty); set => SetValue(ReadingsProperty, value); }

    public IReadOnlyList<Sentence>? Sentences { get => GetValue(SentencesProperty); set => SetValue(SentencesProperty, value); }

    public double TextSize { get => GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }

    public bool ShowChinese { get => GetValue(ShowChineseProperty); set => SetValue(ShowChineseProperty, value); }

    public bool ShowPinyin { get => GetValue(ShowPinyinProperty); set => SetValue(ShowPinyinProperty, value); }

    public bool ShowHanViet { get => GetValue(ShowHanVietProperty); set => SetValue(ShowHanVietProperty, value); }

    public bool HoverHighlight { get => GetValue(HoverHighlightProperty); set => SetValue(HoverHighlightProperty, value); }

    public bool GrammarColors { get => GetValue(GrammarColorsProperty); set => SetValue(GrammarColorsProperty, value); }

    public (int First, int Count)? Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    /// <summary>Where the highlighted sentence starts, from the top of this paragraph (null when nothing is highlighted).</summary>
    public double? HighlightTop()
    {
        if (Highlight is not { } range) return null;
        var block = _words.FirstOrDefault(w => w.Index >= range.First).Block;
        return block?.TranslatePoint(new Point(0, 0), this)?.Y;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HighlightProperty || change.Property == IsPointerOverProperty || change.Property == HoverHighlightProperty
            || change.Property == GrammarColorsProperty || change.Property == SentencesProperty)
            ApplyHighlight();
        else if (change.Property == LineProperty || change.Property == ReadingsProperty || change.Property == TextSizeProperty
                 || change.Property == ShowChineseProperty || change.Property == ShowPinyinProperty || change.Property == ShowHanVietProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        CancelHold();
        Children.Clear();
        _words.Clear();
        if (Line is not { IsSceneBreak: false } line || Readings is not { } readings) return;
        var palette = ReadingPalette.For(ActualThemeVariant);
        var size = TextSize;
        Run? lastVietnamese = null;
        var prefix = "";
        for (var i = 0; i < line.Segments.Count; i++)
        {
            var segment = line.Segments[i];
            var reading = i < readings.Count ? readings[i] : null;
            if (reading is null || !segment.IsWord)
            {
                var text = (segment.Before + segment.Text).Trim();
                if (text.Length == 0) continue;
                // An opening quote goes before the next word, other punctuation after the word before.
                if (text is "“" or "\"" or "「" or "『" or "‘" || lastVietnamese is null) prefix += text;
                else lastVietnamese.Text += text;
                continue;
            }
            var block = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, size * 0.35, size * 0.45),
                Padding = new Thickness(3, 1, 3, 2),
                Tag = i,
                Inlines = [],
            };
            void Row(string value, double rowSize, IBrush? brush)
            {
                var run = new Run(value) { FontSize = rowSize };
                if (brush is not null) run.Foreground = brush;
                block.Inlines!.Add(run);
                block.Inlines.Add(new LineBreak());
            }
            if (ShowChinese && reading.Chinese.Length > 0) Row(reading.Chinese, size * 1.2, null);
            if (ShowPinyin && reading.Pinyin.Length > 0) Row(reading.Pinyin, size * 0.64, palette.Pinyin);
            if (ShowHanViet && reading.HanViet.Length > 0) Row(reading.HanViet, size * 0.64, palette.HanViet);
            // A grammar word with no Vietnamese of its own (的, 般) keeps a quiet dot, so its block stays a word.
            var vietnamese = new Run(prefix + (segment.Text.Length > 0 ? segment.Text : "·")) { FontSize = size };
            prefix = "";
            block.Inlines.Add(vietnamese);
            block.Tapped += OnTapped;
            block.DoubleTapped += OnDoubleTapped;
            block.PointerPressed += OnPressed;
            block.PointerMoved += OnMoved;
            block.PointerReleased += (_, _) => CancelHold();
            block.PointerCaptureLost += (_, _) => CancelHold();
            block.Cursor = new Cursor(StandardCursorType.Hand);
            _words.Add((block, vietnamese, i, segment, reading.Chinese.Length > 0 ? reading.Chinese : line.SourceOf(segment)));
            Children.Add(block);
            lastVietnamese = vietnamese;
        }
        if (prefix.Length > 0 && lastVietnamese is not null) lastVietnamese.Text += prefix;
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        if (Line is not { } line) return;
        var palette = ReadingPalette.For(ActualThemeVariant);
        IBrush? quiet = this.TryFindResource("HelmSecondaryText", ActualThemeVariant, out var dim) ? dim as IBrush : null;
        var range = Highlight;
        var marks = range is null && IsPointerOver && HoverHighlight && Sentences is { } sentences
            ? GrammarTagger.Mark(line, sentences, GrammarColors ? SegmentText.WordClasses : null)
            : null;
        foreach (var (block, vietnamese, index, segment, _) in _words)
        {
            // Never null: a transparent fill makes the whole block tappable, not only its letters.
            block.Background = range is { } r && index >= r.First && index < r.First + r.Count ? palette.Spoken
                : marks is not null && index < marks.Count && marks[index].Band >= 0 ? (marks[index].Dialogue ? palette.Dialogue : palette.Bands[marks[index].Band])
                : Brushes.Transparent;
            vietnamese.ClearValue(TextElement.FontWeightProperty);
            SegmentText.SetForeground(vietnamese, segment.Text.Length == 0 ? quiet : segment.Kind == SegmentKind.Name ? palette.Name : null);
            if (marks is null || !GrammarColors || index >= marks.Count || segment.Text.Length == 0) continue;
            switch (marks[index].Role)
            {
                case GrammarRole.Subject:
                    vietnamese.FontWeight = FontWeight.SemiBold;
                    if (segment.Kind != SegmentKind.Name) vietnamese.Foreground = palette.Subject;
                    break;
                case GrammarRole.Verb:
                    vietnamese.Foreground = palette.Verb;
                    break;
                case GrammarRole.Object:
                    if (segment.Kind != SegmentKind.Name) vietnamese.Foreground = palette.Object;
                    break;
            }
        }
    }

    private (TextBlock Block, Run Vietnamese, int Index, Segment Segment, string Chinese)? WordOf(object? sender) =>
        sender is TextBlock { Tag: int index } ? _words.FirstOrDefault(w => w.Index == index) : null;

    /// <summary>A finger (or the mouse) down on a word: held if it stays there. Not handled, so scrolling still works.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelHold();
        _held = false;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed || WordOf(sender) is not { } word) return;
        _pressedAt = e.GetPosition(this);
        _holdWait = DispatcherTimer.RunOnce(() => OnHeld(word.Block, word.Chinese, word.Index), HoldTime);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_holdWait is null) return;
        var moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) > HoldSlop || Math.Abs(moved.Y) > HoldSlop) CancelHold();
    }

    /// <summary>Holds the word, and flashes it so the press is seen to have worked.</summary>
    private void OnHeld(TextBlock block, string chinese, int index)
    {
        _holdWait = null;
        _held = true;
        _tapWait?.Dispose();
        _tapWait = null;
        block.Background = ReadingPalette.For(ActualThemeVariant).Spoken;
        DispatcherTimer.RunOnce(ApplyHighlight, TimeSpan.FromMilliseconds(500));
        RaiseEvent(new WordHeldEventArgs(WordHeldEvent, chinese, index));
    }

    private void CancelHold()
    {
        _holdWait?.Dispose();
        _holdWait = null;
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (WordOf(sender) is not { } word) return;
        e.Handled = true;
        if (_held)
        {
            // The word was held: lifting the finger opens nothing.
            _held = false;
            return;
        }
        _tapWait?.Dispose();
        _tapWait = DispatcherTimer.RunOnce(() => RaiseEvent(new WordTappedEventArgs(SegmentText.WordTappedEvent, word.Segment, word.Index)), DoubleTapWait);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        _tapWait?.Dispose();
        _tapWait = null;
        if (WordOf(sender) is not { } word) return;
        e.Handled = true;
        if (_held) return;
        RaiseEvent(new WordTappedEventArgs(SegmentText.WordDoubleTappedEvent, word.Segment, word.Index));
    }
}
