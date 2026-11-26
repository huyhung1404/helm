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

/// <summary>A word of a paragraph was tapped (or double-tapped: read aloud from its sentence).</summary>
public sealed class WordTappedEventArgs(RoutedEvent routedEvent, Segment segment, int index) : RoutedEventArgs(routedEvent)
{
    public Segment Segment { get; } = segment;

    /// <summary>The segment's place in the line.</summary>
    public int Index { get; } = index;
}

/// <summary>A word pressed long, with its Chinese (to say) and its segment's place in the line (where its sentence is found).</summary>
public sealed class WordHeldEventArgs(RoutedEvent routedEvent, string chinese, int index) : RoutedEventArgs(routedEvent)
{
    public string Chinese { get; } = chinese;

    public int Index { get; } = index;
}

/// <summary>
/// One converted paragraph as a text block with a run per word, so a tap knows which Chinese it came from. Names are in
/// the accent colour, words no dictionary knows are dimmed, and the sentence being read aloud (or held) is highlighted.
/// A tap (a click) opens the word, a double tap reads aloud from its sentence, and a press held on a word holds its
/// sentence (<see cref="StudyText.WordHeldEvent"/>). Under a mouse (<see cref="HoverHighlight"/>) the word is underlined
/// and the paragraph is marked for reading by eye (<see cref="GrammarTagger"/>): sentences in two alternating shades,
/// what is said in violet, and the subject, verb and object of each clause in colour. Scene breaks show as "* * *".
/// </summary>
public sealed class SegmentText : TextBlock
{
    public static readonly StyledProperty<ConvertedLine?> LineProperty =
        AvaloniaProperty.Register<SegmentText, ConvertedLine?>(nameof(Line));

    /// <summary>The segments of the sentence being read, as (first, count); null highlights nothing.</summary>
    public static readonly StyledProperty<(int First, int Count)?> HighlightProperty =
        AvaloniaProperty.Register<SegmentText, (int First, int Count)?>(nameof(Highlight));

    /// <summary>The paragraph's sentences (as reading aloud cuts them), for the bands.</summary>
    public static readonly StyledProperty<IReadOnlyList<Sentence>?> SentencesProperty =
        AvaloniaProperty.Register<SegmentText, IReadOnlyList<Sentence>?>(nameof(Sentences));

    /// <summary>The paragraph under the mouse is marked for reading by eye.</summary>
    public static readonly StyledProperty<bool> HoverHighlightProperty = AvaloniaProperty.Register<SegmentText, bool>(nameof(HoverHighlight));

    /// <summary>The marked paragraph's subjects, verbs and objects are coloured (needs <see cref="WordClasses"/>).</summary>
    public static readonly StyledProperty<bool> GrammarColorsProperty = AvaloniaProperty.Register<SegmentText, bool>(nameof(GrammarColors));

    /// <summary>Line height as a multiple of the font size.</summary>
    public static readonly StyledProperty<double> RelativeLineHeightProperty =
        AvaloniaProperty.Register<SegmentText, double>(nameof(RelativeLineHeight), 1.6);

    /// <summary>A word was tapped: open it. Bubbles, so the page listens once on the list of paragraphs.</summary>
    public static readonly RoutedEvent<WordTappedEventArgs> WordTappedEvent =
        RoutedEvent.Register<SegmentText, WordTappedEventArgs>(nameof(WordTapped), RoutingStrategies.Bubble);

    /// <summary>A word was double-tapped: read aloud from its sentence.</summary>
    public static readonly RoutedEvent<WordTappedEventArgs> WordDoubleTappedEvent =
        RoutedEvent.Register<SegmentText, WordTappedEventArgs>(nameof(WordDoubleTapped), RoutingStrategies.Bubble);

    /// <summary>A double tap must not open the word first: a single tap waits this long for a second one.</summary>
    private static readonly TimeSpan DoubleTapWait = TimeSpan.FromMilliseconds(230);

    private readonly List<(Run Run, int Index, int Start, bool IsWord)> _runs = [];
    private IDisposable? _tapWait;
    private IDisposable? _holdWait;
    private Point _pressedAt;
    private bool _held;
    private Run? _underlined;
    private IReadOnlyList<SegmentMark>? _marks;
    private bool _marksHaveRoles;

    public SegmentText()
    {
        TextWrapping = TextWrapping.Wrap;
        Tapped += OnTapped;
        DoubleTapped += OnDoubleTapped;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerExited += (_, _) => Underline(null);
        PointerReleased += (_, _) => CancelHold();
        PointerCaptureLost += (_, _) => CancelHold();
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    /// <summary>The word classes the grammar colours come from (set once by the page; null colours no roles).</summary>
    public static IWordClasses? WordClasses { get; set; }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public ConvertedLine? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public (int First, int Count)? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public IReadOnlyList<Sentence>? Sentences
    {
        get => GetValue(SentencesProperty);
        set => SetValue(SentencesProperty, value);
    }

    public bool HoverHighlight
    {
        get => GetValue(HoverHighlightProperty);
        set => SetValue(HoverHighlightProperty, value);
    }

    public bool GrammarColors
    {
        get => GetValue(GrammarColorsProperty);
        set => SetValue(GrammarColorsProperty, value);
    }

    public double RelativeLineHeight
    {
        get => GetValue(RelativeLineHeightProperty);
        set => SetValue(RelativeLineHeightProperty, value);
    }

    public event EventHandler<WordTappedEventArgs>? WordTapped
    {
        add => AddHandler(WordTappedEvent, value);
        remove => RemoveHandler(WordTappedEvent, value);
    }

    public event EventHandler<WordTappedEventArgs>? WordDoubleTapped
    {
        add => AddHandler(WordDoubleTappedEvent, value);
        remove => RemoveHandler(WordDoubleTappedEvent, value);
    }

    /// <summary>A word pressed long: the same event as <see cref="StudyText.WordHeld"/>, so the page handles both alike.</summary>
    public event EventHandler<WordHeldEventArgs>? WordHeld
    {
        add => AddHandler(StudyText.WordHeldEvent, value);
        remove => RemoveHandler(StudyText.WordHeldEvent, value);
    }

    /// <summary>Where the highlighted sentence starts, from the top of this block (null when nothing is highlighted).</summary>
    public double? HighlightTop()
    {
        if (Highlight is not { } range) return null;
        foreach (var (_, index, start, _) in _runs)
        {
            if (index < range.First) continue;
            return TextLayout.HitTestTextPosition(start).Top + Padding.Top;
        }
        return null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty) Rebuild();
        else if (change.Property == SentencesProperty)
        {
            _marks = null;
            ApplyHighlight();
        }
        else if (change.Property == HighlightProperty || change.Property == HoverHighlightProperty || change.Property == GrammarColorsProperty
                 || change.Property == IsPointerOverProperty)
            ApplyHighlight();
        else if (change.Property == FontSizeProperty || change.Property == RelativeLineHeightProperty) LineHeight = Math.Round(FontSize * RelativeLineHeight);
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        _runs.Clear();
        _marks = null;
        _underlined = null;
        TextAlignment = TextAlignment.Left;
        if (Line is not { } line) return;
        if (line.IsSceneBreak)
        {
            TextAlignment = TextAlignment.Center;
            inlines.Add(new Run(line.Text));
            return;
        }
        var start = 0;
        for (var i = 0; i < line.Segments.Count; i++)
        {
            var segment = line.Segments[i];
            if (segment.Before.Length > 0)
            {
                var space = new Run(segment.Before);
                _runs.Add((space, i, start, false));
                inlines.Add(space);
                start += segment.Before.Length;
            }
            if (segment.Text.Length == 0) continue;
            var run = new Run(segment.Text);
            _runs.Add((run, i, start, true));
            inlines.Add(run);
            start += segment.Text.Length;
        }
        ApplyHighlight();
    }

    /// <summary>The marks for reading by eye, worked out the first time the mouse comes over the paragraph.</summary>
    private IReadOnlyList<SegmentMark>? Marks()
    {
        if (Line is not { IsSceneBreak: false } line || Sentences is not { } sentences) return null;
        var classes = GrammarColors ? WordClasses : null;
        var withRoles = classes is { IsReady: true };
        // Worked out before the word classes were loaded: again now, with the roles.
        if (_marks is null || withRoles && !_marksHaveRoles)
        {
            _marks = GrammarTagger.Mark(line, sentences, classes);
            _marksHaveRoles = withRoles;
        }
        return _marks;
    }

    private void ApplyHighlight()
    {
        if (Line is not { } line) return;
        var palette = ReadingPalette.For(ActualThemeVariant);
        IBrush? quiet = this.TryFindResource("HelmSecondaryText", ActualThemeVariant, out var dim) ? dim as IBrush : null;
        var range = Highlight;
        // The sentence being read aloud keeps its own highlight; the paragraph under the mouse is marked otherwise.
        var marks = range is null && IsPointerOver && HoverHighlight ? Marks() : null;
        foreach (var (run, index, _, isWord) in _runs)
        {
            // The space before the sentence's first word stays plain, so the highlight hugs the words.
            var inside = range is { } r && index >= r.First && index < r.First + r.Count && !(index == r.First && !isWord);
            if (inside) run.Background = palette.Spoken;
            else if (marks is not null && index < marks.Count && marks[index] is var mark && mark.Band >= 0 && !(mark.StartsSentence && !isWord))
                run.Background = mark.Dialogue ? palette.Dialogue : palette.Bands[mark.Band];
            else run.Background = null;

            if (!isWord) continue;
            var segment = line.Segments[index];
            // Plain words take the text's own colour: a cleared value, never null (a null brush draws nothing).
            run.ClearValue(TextElement.FontWeightProperty);
            SetForeground(run, segment.Kind == SegmentKind.Name ? palette.Name : segment.Kind == SegmentKind.Unknown ? quiet : null);
            if (marks is null || !GrammarColors || index >= marks.Count || !segment.IsWord) continue;
            switch (marks[index].Role)
            {
                case GrammarRole.Subject:
                    run.FontWeight = FontWeight.SemiBold;
                    if (segment.Kind != SegmentKind.Name) run.Foreground = palette.Subject;
                    break;
                case GrammarRole.Verb:
                    run.Foreground = palette.Verb;
                    break;
                case GrammarRole.Object:
                    if (segment.Kind != SegmentKind.Name) run.Foreground = palette.Object;
                    break;
                case GrammarRole.Modifier:
                    if (segment.Kind != SegmentKind.Name) SetForeground(run, quiet);
                    break;
            }
        }
    }

    /// <summary>A run's own colour, or the text's colour when <paramref name="brush"/> is null.</summary>
    internal static void SetForeground(TextElement run, IBrush? brush)
    {
        if (brush is null) run.ClearValue(TextElement.ForegroundProperty);
        else run.Foreground = brush;
    }

    /// <summary>The word under a point of this block, or null (a space, punctuation, past the end of a line).</summary>
    private (Segment Segment, int Index, Run Run)? WordAt(Point point)
    {
        if (Line is not { IsSceneBreak: false } line || _runs.Count == 0) return null;
        var hit = TextLayout.HitTestPoint(new Point(point.X - Padding.Left, point.Y - Padding.Top));
        if (!hit.IsInside) return null;
        var position = hit.TextPosition;
        foreach (var (run, index, start, isWord) in _runs)
        {
            var length = run.Text?.Length ?? 0;
            if (position < start || position >= start + length) continue;
            var segment = line.Segments[index];
            return isWord && segment.IsWord ? (segment, index, run) : null;
        }
        return null;
    }

    /// <summary>The word under the mouse is underlined and shows a hand: it can be clicked.</summary>
    private void Underline(Run? run)
    {
        if (ReferenceEquals(run, _underlined)) return;
        if (_underlined is not null) _underlined.TextDecorations = null;
        _underlined = run;
        if (run is not null) run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        Cursor = run is null ? null : new Cursor(StandardCursorType.Hand);
    }

    /// <summary>A finger down on a word: held if it stays there. Not handled, so scrolling still works.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelHold();
        _held = false;
        _pressedAt = e.GetPosition(this);
        // The right button is the paragraph's menu, not a hold.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (WordAt(_pressedAt) is not { } word || Line is not { } line) return;
        _holdWait = DispatcherTimer.RunOnce(() =>
        {
            _holdWait = null;
            _held = true;
            _tapWait?.Dispose();
            _tapWait = null;
            RaiseEvent(new WordHeldEventArgs(StudyText.WordHeldEvent, line.SourceOf(word.Segment), word.Index));
        }, StudyText.HoldTime);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse) Underline(WordAt(e.GetPosition(this))?.Run);
        if (_holdWait is null) return;
        var moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) > StudyText.HoldSlop || Math.Abs(moved.Y) > StudyText.HoldSlop) CancelHold();
    }

    private void CancelHold()
    {
        _holdWait?.Dispose();
        _holdWait = null;
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (WordAt(e.GetPosition(this)) is not { } word) return;
        e.Handled = true;
        if (_held)
        {
            // The word was held: lifting the finger opens nothing.
            _held = false;
            return;
        }
        _tapWait?.Dispose();
        _tapWait = DispatcherTimer.RunOnce(() => RaiseEvent(new WordTappedEventArgs(WordTappedEvent, word.Segment, word.Index)), DoubleTapWait);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        _tapWait?.Dispose();
        _tapWait = null;
        if (_held) return;
        var point = e.GetPosition(this);
        (Segment Segment, int Index)? word = WordAt(point) is { } at ? (at.Segment, at.Index) : null;
        if (word is null && Line is { IsSceneBreak: false } line && line.Segments.Count > 0)
        {
            // Between words: the nearest segment of the line.
            var position = TextLayout.HitTestPoint(new Point(point.X - Padding.Left, point.Y - Padding.Top)).TextPosition;
            var nearest = _runs.LastOrDefault(r => r.Start <= position);
            word = (line.Segments[Math.Clamp(nearest.Index, 0, line.Segments.Count - 1)], nearest.Index);
        }
        if (word is not { } found) return;
        e.Handled = true;
        RaiseEvent(new WordTappedEventArgs(WordDoubleTappedEvent, found.Segment, found.Index));
    }
}
