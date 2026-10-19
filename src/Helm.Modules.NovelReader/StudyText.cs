using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.NovelReader;

/// <summary>
/// A paragraph in study mode: one block per word, wrapping like text, with the word's Chinese, pinyin, Hán Việt and
/// Vietnamese on four centred rows (each row can be hidden). Punctuation joins the Vietnamese of the word before it. A
/// click opens the word popup and a double click reads aloud from there, as on <see cref="SegmentText"/>; holding the
/// mouse down on a word says its Chinese (<see cref="WordHeldEvent"/>), with no popup when it is let go. The sentence
/// being read is highlighted, and under the mouse the sentences and grammar are marked on the Vietnamese row. Each block
/// is a single text block, so a long chapter stays light.
/// </summary>
public sealed class StudyText : WrapPanel
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(ConvertedLine), typeof(StudyText),
        new PropertyMetadata(null, (d, _) => ((StudyText)d).Rebuild()));

    public static readonly DependencyProperty ReadingsProperty = DependencyProperty.Register(nameof(Readings), typeof(IReadOnlyList<WordReading?>),
        typeof(StudyText), new PropertyMetadata(null, (d, _) => ((StudyText)d).Rebuild()));

    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(nameof(TextSize), typeof(double), typeof(StudyText),
        new PropertyMetadata(18.0, (d, _) => ((StudyText)d).Rebuild()));

    public static readonly DependencyProperty ShowChineseProperty = DependencyProperty.Register(nameof(ShowChinese), typeof(bool), typeof(StudyText),
        new PropertyMetadata(true, (d, _) => ((StudyText)d).Rebuild()));

    public static readonly DependencyProperty ShowPinyinProperty = DependencyProperty.Register(nameof(ShowPinyin), typeof(bool), typeof(StudyText),
        new PropertyMetadata(true, (d, _) => ((StudyText)d).Rebuild()));

    public static readonly DependencyProperty ShowHanVietProperty = DependencyProperty.Register(nameof(ShowHanViet), typeof(bool), typeof(StudyText),
        new PropertyMetadata(true, (d, _) => ((StudyText)d).Rebuild()));

    /// <summary>The segments of the sentence being read, as (first, count); null highlights nothing.</summary>
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof((int First, int Count)?),
        typeof(StudyText), new PropertyMetadata(null, (d, _) => ((StudyText)d).Apply()));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(nameof(HighlightBrush), typeof(Brush),
        typeof(StudyText), new PropertyMetadata(null, (d, _) => ((StudyText)d).Apply()));

    public static readonly DependencyProperty SentencesProperty = DependencyProperty.Register(nameof(Sentences), typeof(IReadOnlyList<Sentence>),
        typeof(StudyText), new PropertyMetadata(null, (d, _) => ((StudyText)d)._marks = null));

    public static readonly DependencyProperty IsHotProperty = DependencyProperty.Register(nameof(IsHot), typeof(bool), typeof(StudyText),
        new PropertyMetadata(false, (d, _) => ((StudyText)d).Apply()));

    public static readonly DependencyProperty HoverHighlightProperty = DependencyProperty.Register(nameof(HoverHighlight), typeof(bool),
        typeof(StudyText), new PropertyMetadata(false, (d, _) => ((StudyText)d).Apply()));

    public static readonly DependencyProperty GrammarColorsProperty = DependencyProperty.Register(nameof(GrammarColors), typeof(bool),
        typeof(StudyText), new PropertyMetadata(false, (d, _) => ((StudyText)d).Apply()));

    /// <summary>The mouse held down on a word this long says its Chinese.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(450);

    /// <summary>A word was held: say its Chinese.</summary>
    public static readonly RoutedEvent WordHeldEvent = EventManager.RegisterRoutedEvent(nameof(WordHeld), RoutingStrategy.Bubble,
        typeof(EventHandler<WordHeldEventArgs>), typeof(StudyText));

    private readonly List<Word> _words = [];
    private readonly DispatcherTimer _hold = new() { Interval = HoldTime };
    private IReadOnlyList<SegmentMark>? _marks;
    private bool _marksHaveRoles;
    private bool _doubleClicked;
    private Word? _pressed;
    private bool _held;

    /// <summary>A block, the segment it shows, its Chinese and its Vietnamese row (where marks and colours go).</summary>
    private sealed record Word(TextBlock Block, int Index, Segment Segment, string Chinese, Run Vietnamese);

    public StudyText()
    {
        Loaded += (_, _) => Rebuild();
        Unloaded += (_, _) => CancelHold();
        _hold.Tick += (_, _) => OnHeld();
    }

    public event EventHandler<WordHeldEventArgs> WordHeld
    {
        add => AddHandler(WordHeldEvent, value);
        remove => RemoveHandler(WordHeldEvent, value);
    }

    public ConvertedLine? Line { get => (ConvertedLine?)GetValue(LineProperty); set => SetValue(LineProperty, value); }

    public IReadOnlyList<WordReading?>? Readings { get => (IReadOnlyList<WordReading?>?)GetValue(ReadingsProperty); set => SetValue(ReadingsProperty, value); }

    public double TextSize { get => (double)GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }

    public bool ShowChinese { get => (bool)GetValue(ShowChineseProperty); set => SetValue(ShowChineseProperty, value); }

    public bool ShowPinyin { get => (bool)GetValue(ShowPinyinProperty); set => SetValue(ShowPinyinProperty, value); }

    public bool ShowHanViet { get => (bool)GetValue(ShowHanVietProperty); set => SetValue(ShowHanVietProperty, value); }

    public (int First, int Count)? Highlight { get => ((int First, int Count)?)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    public Brush? HighlightBrush { get => (Brush?)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    public IReadOnlyList<Sentence>? Sentences { get => (IReadOnlyList<Sentence>?)GetValue(SentencesProperty); set => SetValue(SentencesProperty, value); }

    public bool IsHot { get => (bool)GetValue(IsHotProperty); set => SetValue(IsHotProperty, value); }

    public bool HoverHighlight { get => (bool)GetValue(HoverHighlightProperty); set => SetValue(HoverHighlightProperty, value); }

    public bool GrammarColors { get => (bool)GetValue(GrammarColorsProperty); set => SetValue(GrammarColorsProperty, value); }

    /// <summary>A word was clicked: the same event as <see cref="SegmentText.WordClicked"/>, so the page handles both alike.</summary>
    public event EventHandler<WordClickedEventArgs> WordClicked
    {
        add => AddHandler(SegmentText.WordClickedEvent, value);
        remove => RemoveHandler(SegmentText.WordClickedEvent, value);
    }

    public event EventHandler<WordClickedEventArgs> ReadFromClicked
    {
        add => AddHandler(SegmentText.ReadFromClickedEvent, value);
        remove => RemoveHandler(SegmentText.ReadFromClickedEvent, value);
    }

    /// <summary>Where the highlighted sentence starts, from the top of this paragraph (null when nothing is highlighted).</summary>
    public double? HighlightTop()
    {
        if (Highlight is not { } range) return null;
        var word = _words.FirstOrDefault(w => w.Index >= range.First);
        return word?.Block.IsLoaded == true ? word.Block.TranslatePoint(new Point(0, 0), this).Y : null;
    }

    private void Rebuild()
    {
        CancelHold();
        Children.Clear();
        _words.Clear();
        _marks = null;
        if (Line is not { IsSceneBreak: false } line || Readings is not { } readings || !IsLoaded) return;
        var palette = ReadingPalette.For(this);
        var size = TextSize;
        Word? last = null;
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
                if (text is "“" or "\"" or "「" or "『" or "‘" || last is null) prefix += text;
                else last.Vietnamese.Text += text;
                continue;
            }
            var block = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, size * 0.35, size * 0.45),
                Padding = new Thickness(3, 1, 3, 2),
                Cursor = Cursors.Hand,
                Tag = i,
            };
            if (ShowChinese && reading.Chinese.Length > 0)
                AddRow(block, reading.Chinese, size * 1.2, new FontFamily("Microsoft YaHei UI, Microsoft YaHei, SimSun"), null);
            if (ShowPinyin && reading.Pinyin.Length > 0) AddRow(block, reading.Pinyin, size * 0.64, null, palette.Pinyin);
            if (ShowHanViet && reading.HanViet.Length > 0) AddRow(block, reading.HanViet, size * 0.64, null, palette.HanViet);
            // A grammar word with no Vietnamese of its own (的, 般) keeps a quiet dot, so its block stays a word.
            var vietnamese = new Run(prefix + (segment.Text.Length > 0 ? segment.Text : "·")) { FontSize = size };
            if (segment.Text.Length == 0) vietnamese.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
            else if (segment.Kind == SegmentKind.Name) vietnamese.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            prefix = "";
            block.Inlines.Add(vietnamese);
            block.MouseLeftButtonDown += OnMouseDown;
            block.MouseLeftButtonUp += OnMouseUp;
            block.MouseLeave += (_, _) => CancelHold();
            var word = new Word(block, i, segment, reading.Chinese.Length > 0 ? reading.Chinese : line.SourceOf(segment), vietnamese);
            _words.Add(word);
            Children.Add(block);
            last = word;
        }
        if (prefix.Length > 0 && last is not null) last.Vietnamese.Text += prefix;
        Apply();
    }

    private static void AddRow(TextBlock block, string text, double size, FontFamily? font, Brush? brush)
    {
        var run = new Run(text) { FontSize = size };
        if (font is not null) run.FontFamily = font;
        if (brush is not null) run.Foreground = brush;
        block.Inlines.Add(run);
        block.Inlines.Add(new LineBreak());
    }

    private IReadOnlyList<SegmentMark>? Marks()
    {
        if (Line is not { IsSceneBreak: false } line || Sentences is not { } sentences) return null;
        var classes = GrammarColors ? SegmentText.WordClasses : null;
        var withRoles = classes is { IsReady: true };
        if (_marks is null || withRoles && !_marksHaveRoles)
        {
            _marks = GrammarTagger.Mark(line, sentences, classes);
            _marksHaveRoles = withRoles;
        }
        return _marks;
    }

    /// <summary>The sentence read aloud, or the marks of the paragraph under the mouse, on the blocks.</summary>
    private void Apply()
    {
        var range = Highlight;
        var marks = range is null && IsHot && HoverHighlight ? Marks() : null;
        var palette = marks is null ? null : ReadingPalette.For(this);
        foreach (var word in _words)
        {
            var inside = range is { } r && word.Index >= r.First && word.Index < r.First + r.Count;
            if (inside && HighlightBrush is { } brush) word.Block.Background = brush;
            else if (marks is not null && word.Index < marks.Count && marks[word.Index].Band >= 0)
                word.Block.Background = marks[word.Index].Dialogue ? palette!.Dialogue : palette!.Bands[marks[word.Index].Band];
            // Never cleared: a transparent fill makes the whole block clickable, not only its letters.
            else word.Block.Background = Brushes.Transparent;

            word.Vietnamese.ClearValue(TextElement.FontWeightProperty);
            if (word.Segment.Text.Length == 0) continue;
            if (word.Segment.Kind == SegmentKind.Name) word.Vietnamese.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            else word.Vietnamese.ClearValue(TextElement.ForegroundProperty);
            if (marks is null || !GrammarColors || word.Index >= marks.Count) continue;
            switch (marks[word.Index].Role)
            {
                case GrammarRole.Subject:
                    word.Vietnamese.FontWeight = FontWeights.SemiBold;
                    if (word.Segment.Kind != SegmentKind.Name) word.Vietnamese.Foreground = palette!.Subject;
                    break;
                case GrammarRole.Verb:
                    word.Vietnamese.Foreground = palette!.Verb;
                    break;
                case GrammarRole.Object:
                    if (word.Segment.Kind != SegmentKind.Name) word.Vietnamese.Foreground = palette!.Object;
                    break;
                case GrammarRole.Modifier:
                    if (word.Segment.Kind != SegmentKind.Name) word.Vietnamese.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                    break;
            }
        }
    }

    private Word? WordOf(object sender) => sender is TextBlock { Tag: int index } ? _words.FirstOrDefault(w => w.Index == index) : null;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (WordOf(sender) is not { } word) return;
        if (e.ClickCount == 1)
        {
            _held = false;
            _pressed = word;
            _hold.Stop();
            _hold.Start();
            return;
        }
        if (e.ClickCount != 2) return;
        CancelHold();
        _doubleClicked = true;
        RaiseEvent(new WordClickedEventArgs(SegmentText.ReadFromClickedEvent, word.Segment, word.Index));
        e.Handled = true;
    }

    /// <summary>The mouse stayed down on the word: say it, and flash the word so the press is seen to have worked.</summary>
    private void OnHeld()
    {
        _hold.Stop();
        if (_pressed is not { } word || Mouse.LeftButton != MouseButtonState.Pressed) return;
        _held = true;
        word.Block.Background = HighlightBrush ?? SystemColors.HighlightBrush;
        var flash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        flash.Tick += (_, _) =>
        {
            flash.Stop();
            Apply();
        };
        flash.Start();
        RaiseEvent(new WordHeldEventArgs(WordHeldEvent, word.Chinese));
    }

    private void CancelHold()
    {
        _hold.Stop();
        _pressed = null;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        CancelHold();
        if (_held)
        {
            // The word was said: letting go opens nothing.
            _held = false;
            e.Handled = true;
            return;
        }
        if (_doubleClicked)
        {
            _doubleClicked = false;
            e.Handled = true;
            return;
        }
        if (WordOf(sender) is not { } word) return;
        RaiseEvent(new WordClickedEventArgs(SegmentText.WordClickedEvent, word.Segment, word.Index));
        e.Handled = true;
    }
}

/// <summary>A word held down in study mode, with the Chinese to say.</summary>
public sealed class WordHeldEventArgs(RoutedEvent routedEvent, string chinese) : RoutedEventArgs(routedEvent)
{
    public string Chinese { get; } = chinese;
}
