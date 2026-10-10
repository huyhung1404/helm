using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Helm.Modules.NovelReader.Conversion;

namespace Helm.Modules.NovelReader;

/// <summary>A word of a paragraph was tapped (or double-tapped: read aloud from its sentence).</summary>
public sealed class WordTappedEventArgs(RoutedEvent routedEvent, Segment segment, int index) : RoutedEventArgs(routedEvent)
{
    public Segment Segment { get; } = segment;

    /// <summary>The segment's place in the line.</summary>
    public int Index { get; } = index;
}

/// <summary>
/// One converted paragraph as a text block with a run per word, so a tap knows which Chinese it came from (the Avalonia
/// counterpart of the Windows SegmentText). Names are in the accent colour, words no dictionary knows are dimmed, and
/// the sentence being read aloud is highlighted. A tap opens the word; a double tap reads aloud from its sentence.
/// Scene breaks show as a centred "* * *".
/// </summary>
public sealed class SegmentText : TextBlock
{
    public static readonly StyledProperty<ConvertedLine?> LineProperty =
        AvaloniaProperty.Register<SegmentText, ConvertedLine?>(nameof(Line));

    /// <summary>The segments of the sentence being read, as (first, count); null highlights nothing.</summary>
    public static readonly StyledProperty<(int First, int Count)?> HighlightProperty =
        AvaloniaProperty.Register<SegmentText, (int First, int Count)?>(nameof(Highlight));

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

    public SegmentText()
    {
        TextWrapping = TextWrapping.Wrap;
        Tapped += OnTapped;
        DoubleTapped += OnDoubleTapped;
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

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
        else if (change.Property == HighlightProperty) ApplyHighlight();
        else if (change.Property == FontSizeProperty || change.Property == RelativeLineHeightProperty) LineHeight = Math.Round(FontSize * RelativeLineHeight);
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        _runs.Clear();
        TextAlignment = TextAlignment.Left;
        if (Line is not { } line) return;
        if (line.IsSceneBreak)
        {
            TextAlignment = TextAlignment.Center;
            inlines.Add(new Run(line.Text));
            return;
        }
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var name = new SolidColorBrush(Accent(dark ? "SystemAccentColorLight1" : "SystemAccentColorDark1"));
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
            if (segment.Kind == SegmentKind.Name) run.Foreground = name;
            else if (segment.Kind == SegmentKind.Unknown) run.Foreground = this.TryFindResource("HelmSecondaryText", ActualThemeVariant, out var dim) ? dim as IBrush : null;
            _runs.Add((run, i, start, true));
            inlines.Add(run);
            start += segment.Text.Length;
        }
        ApplyHighlight();
    }

    private Color Accent(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : Color.FromUInt32(0xFF0067C0);

    private void ApplyHighlight()
    {
        var range = Highlight;
        var fill = new SolidColorBrush(Accent("SystemAccentColor"), 0.38);
        foreach (var (run, index, _, isWord) in _runs)
        {
            // The space before the sentence's first word stays plain, so the highlight hugs the words.
            var inside = range is { } r && index >= r.First && index < r.First + r.Count && !(index == r.First && !isWord);
            run.Background = inside ? fill : null;
        }
    }

    /// <summary>The word under a point of this block, or null (a space, punctuation, past the end of a line).</summary>
    private (Segment Segment, int Index)? WordAt(Point point)
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
            return isWord && segment.IsWord ? (segment, index) : null;
        }
        return null;
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (WordAt(e.GetPosition(this)) is not { } word) return;
        e.Handled = true;
        _tapWait?.Dispose();
        _tapWait = DispatcherTimer.RunOnce(() => RaiseEvent(new WordTappedEventArgs(WordTappedEvent, word.Segment, word.Index)), DoubleTapWait);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        _tapWait?.Dispose();
        _tapWait = null;
        var point = e.GetPosition(this);
        var word = WordAt(point);
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
