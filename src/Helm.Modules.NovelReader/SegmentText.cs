using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Modules.NovelReader.Conversion;

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
/// Scene breaks show as a centred "* * *".
/// </summary>
public sealed class SegmentText : TextBlock
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(ConvertedLine), typeof(SegmentText),
        new PropertyMetadata(null, (d, _) => ((SegmentText)d).Rebuild()));

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
                if (segment.Kind == SegmentKind.Name) run.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                else if (segment.Kind == SegmentKind.Unknown) run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
            }
            _runs.Add((run, i));
            Inlines.Add(run);
        }
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        var range = Highlight;
        foreach (var (run, index) in _runs)
        {
            // The space before the sentence's first word stays plain, so the highlight hugs the words.
            var inside = range is { } r && index >= r.First && index < r.First + r.Count && !(index == r.First && run.Tag is null);
            if (inside && HighlightBrush is { } brush) run.Background = brush;
            else run.ClearValue(TextElement.BackgroundProperty);
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
