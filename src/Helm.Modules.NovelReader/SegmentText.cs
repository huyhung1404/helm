using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.NovelReader;

/// <summary>A word of a paragraph was clicked (or double-clicked: read aloud from its sentence).</summary>
public sealed class WordClickedEventArgs(RoutedEvent routedEvent, Segment segment, int index) : RoutedEventArgs(routedEvent)
{
    public Segment Segment { get; } = segment;

    /// <summary>The segment's place in the line.</summary>
    public int Index { get; } = index;
}

/// <summary>
/// One converted paragraph as a text block with a run per word, so a click knows which Chinese it came from. Names
/// are in the accent colour; the word under the mouse is highlighted, and so is the sentence being read aloud.
/// While the mouse is over the paragraph (<see cref="IsHot"/>) it is marked for reading by eye (<see cref="GrammarTagger"/>):
/// sentences in two alternating shades, what is said in violet, and the subject, verb and object of each clause in
/// colour. Scene breaks show as a centred "* * *".
/// </summary>
public sealed class SegmentText : TextBlock
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(ConvertedLine), typeof(SegmentText),
        new PropertyMetadata(null, (d, _) => ((SegmentText)d).Rebuild()));

    /// <summary>The paragraph's sentences (as reading aloud cuts them), for the bands.</summary>
    public static readonly DependencyProperty SentencesProperty = DependencyProperty.Register(nameof(Sentences), typeof(IReadOnlyList<Sentence>),
        typeof(SegmentText), new PropertyMetadata(null, (d, _) => ((SegmentText)d).ForgetMarks()));

    /// <summary>The mouse is over the paragraph.</summary>
    public static readonly DependencyProperty IsHotProperty = DependencyProperty.Register(nameof(IsHot), typeof(bool), typeof(SegmentText),
        new PropertyMetadata(false, (d, _) => ((SegmentText)d).ApplyHighlight()));

    public static readonly DependencyProperty HoverHighlightProperty = DependencyProperty.Register(nameof(HoverHighlight), typeof(bool),
        typeof(SegmentText), new PropertyMetadata(false, (d, _) => ((SegmentText)d).ApplyHighlight()));

    public static readonly DependencyProperty GrammarColorsProperty = DependencyProperty.Register(nameof(GrammarColors), typeof(bool),
        typeof(SegmentText), new PropertyMetadata(false, (d, _) => ((SegmentText)d).ApplyHighlight()));

    /// <summary>The word classes the grammar colours come from (set once by the page; null colours no roles).</summary>
    public static IWordClasses? WordClasses { get; set; }

    /// <summary>The segments of the sentence being read, as (first, count); null highlights nothing.</summary>
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof((int First, int Count)?),
        typeof(SegmentText), new PropertyMetadata(null, (d, _) => ((SegmentText)d).ApplyHighlight()));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(nameof(HighlightBrush), typeof(Brush),
        typeof(SegmentText), new PropertyMetadata(null, (d, _) => ((SegmentText)d).ApplyHighlight()));

    public static readonly RoutedEvent WordClickedEvent = EventManager.RegisterRoutedEvent(nameof(WordClicked), RoutingStrategy.Bubble,
        typeof(EventHandler<WordClickedEventArgs>), typeof(SegmentText));

    public static readonly RoutedEvent ReadFromClickedEvent = EventManager.RegisterRoutedEvent(nameof(ReadFromClicked), RoutingStrategy.Bubble,
        typeof(EventHandler<WordClickedEventArgs>), typeof(SegmentText));

    private readonly List<(Run Run, int Index)> _runs = [];
    private bool _doubleClicked;
    private IReadOnlyList<SegmentMark>? _marks;
    private bool _marksHaveRoles;

    public SegmentText()
    {
        TextWrapping = TextWrapping.Wrap;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeftButtonDown += OnMouseDown;
    }

    public ConvertedLine? Line
    {
        get => (ConvertedLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public (int First, int Count)? Highlight
    {
        get => ((int First, int Count)?)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public IReadOnlyList<Sentence>? Sentences
    {
        get => (IReadOnlyList<Sentence>?)GetValue(SentencesProperty);
        set => SetValue(SentencesProperty, value);
    }

    public bool IsHot
    {
        get => (bool)GetValue(IsHotProperty);
        set => SetValue(IsHotProperty, value);
    }

    public bool HoverHighlight
    {
        get => (bool)GetValue(HoverHighlightProperty);
        set => SetValue(HoverHighlightProperty, value);
    }

    public bool GrammarColors
    {
        get => (bool)GetValue(GrammarColorsProperty);
        set => SetValue(GrammarColorsProperty, value);
    }

    public event EventHandler<WordClickedEventArgs> WordClicked
    {
        add => AddHandler(WordClickedEvent, value);
        remove => RemoveHandler(WordClickedEvent, value);
    }

    public event EventHandler<WordClickedEventArgs> ReadFromClicked
    {
        add => AddHandler(ReadFromClickedEvent, value);
        remove => RemoveHandler(ReadFromClickedEvent, value);
    }

    /// <summary>Where the highlighted sentence starts, from the top of this block (null when nothing is highlighted).</summary>
    public double? HighlightTop()
    {
        if (Highlight is not { } range) return null;
        foreach (var (run, index) in _runs)
        {
            if (index < range.First) continue;
            var rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            return rect.IsEmpty ? null : rect.Top;
        }
        return null;
    }

    private void Rebuild()
    {
        Inlines.Clear();
        _runs.Clear();
        _marks = null;
        TextAlignment = TextAlignment.Left;
        if (Line is not { } line) return;
        if (line.IsSceneBreak)
        {
            TextAlignment = TextAlignment.Center;
            Inlines.Add(new Run(line.Text));
            return;
        }
        for (var i = 0; i < line.Segments.Count; i++)
        {
            var segment = line.Segments[i];
            if (segment.Before.Length > 0)
            {
                var space = new Run(segment.Before);
                _runs.Add((space, i));
                Inlines.Add(space);
            }
            if (segment.Text.Length == 0) continue;
            var run = new Run(segment.Text) { Tag = segment };
            if (segment.IsWord)
            {
                run.Cursor = Cursors.Hand;
                run.MouseEnter += (_, _) => run.TextDecorations = System.Windows.TextDecorations.Underline;
                run.MouseLeave += (_, _) => run.TextDecorations = null;
            }
            ResetForeground(run, segment);
            _runs.Add((run, i));
            Inlines.Add(run);
        }
        ApplyHighlight();
    }

    /// <summary>A word's own look: names in the accent colour, words without a reading quieter.</summary>
    private static void ResetForeground(Run run, Segment segment)
    {
        run.ClearValue(TextElement.FontWeightProperty);
        if (segment.Kind == SegmentKind.Name) run.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        else if (segment.Kind == SegmentKind.Unknown) run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        else run.ClearValue(TextElement.ForegroundProperty);
    }

    private void ForgetMarks() => _marks = null;

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
        var range = Highlight;
        // The sentence being read aloud keeps its own highlight; the paragraph under the mouse is marked otherwise.
        var marks = range is null && IsHot && HoverHighlight ? Marks() : null;
        var palette = marks is null ? null : ReadingPalette.For(this);
        foreach (var (run, index) in _runs)
        {
            // The space before the sentence's first word stays plain, so the highlight hugs the words.
            var inside = range is { } r && index >= r.First && index < r.First + r.Count && !(index == r.First && run.Tag is null);
            if (inside && HighlightBrush is { } brush) run.Background = brush;
            else if (marks is not null && index < marks.Count && marks[index] is var mark && mark.Band >= 0 && !(mark.StartsSentence && run.Tag is null))
                run.Background = mark.Dialogue ? palette!.Dialogue : palette!.Bands[mark.Band];
            else run.ClearValue(TextElement.BackgroundProperty);

            if (run.Tag is not Segment segment) continue;
            ResetForeground(run, segment);
            if (marks is null || !GrammarColors || index >= marks.Count || !segment.IsWord) continue;
            switch (marks[index].Role)
            {
                case GrammarRole.Subject:
                    run.FontWeight = FontWeights.SemiBold;
                    if (segment.Kind != SegmentKind.Name) run.Foreground = palette!.Subject;
                    break;
                case GrammarRole.Verb:
                    run.Foreground = palette!.Verb;
                    break;
                case GrammarRole.Object:
                    if (segment.Kind != SegmentKind.Name) run.Foreground = palette!.Object;
                    break;
                case GrammarRole.Modifier:
                    if (segment.Kind != SegmentKind.Name) run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                    break;
            }
        }
    }

    private int IndexOf(Run run)
    {
        foreach (var (r, index) in _runs)
            if (ReferenceEquals(r, run)) return index;
        return -1;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || e.OriginalSource is not Run run || IndexOf(run) is var index && index < 0) return;
        _doubleClicked = true;
        RaiseEvent(new WordClickedEventArgs(ReadFromClickedEvent, (run.Tag as Segment) ?? Line!.Segments[index], index));
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // The second release of a double-click does not open the word popup again.
        if (_doubleClicked)
        {
            _doubleClicked = false;
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is Run { Tag: Segment segment } run && segment.IsWord)
        {
            RaiseEvent(new WordClickedEventArgs(WordClickedEvent, segment, IndexOf(run)));
            e.Handled = true;
        }
    }
}
