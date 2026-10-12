using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Helm.Modules.NovelReader.Conversion;

namespace Helm.Modules.NovelReader;

/// <summary>
/// A paragraph in study mode (the Avalonia counterpart of the Windows StudyText): one block per word, wrapping like text,
/// with the word's Chinese, pinyin, Hán Việt and Vietnamese on four centred rows (each row can be hidden). Punctuation
/// joins the Vietnamese of the word before it. A tap opens the word and a double tap reads aloud from there, through
/// <see cref="SegmentText"/>'s events, and the sentence being read is highlighted. Each block is one text block.
/// </summary>
public sealed class StudyText : WrapPanel
{
    public static readonly StyledProperty<ConvertedLine?> LineProperty = AvaloniaProperty.Register<StudyText, ConvertedLine?>(nameof(Line));

    public static readonly StyledProperty<IReadOnlyList<WordReading?>?> ReadingsProperty =
        AvaloniaProperty.Register<StudyText, IReadOnlyList<WordReading?>?>(nameof(Readings));

    public static readonly StyledProperty<double> TextSizeProperty = AvaloniaProperty.Register<StudyText, double>(nameof(TextSize), 18);

    public static readonly StyledProperty<bool> ShowChineseProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(ShowChinese), true);

    public static readonly StyledProperty<bool> ShowPinyinProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(ShowPinyin), true);

    public static readonly StyledProperty<bool> ShowHanVietProperty = AvaloniaProperty.Register<StudyText, bool>(nameof(ShowHanViet), true);

    /// <summary>The segments of the sentence being read, as (first, count); null highlights nothing.</summary>
    public static readonly StyledProperty<(int First, int Count)?> HighlightProperty =
        AvaloniaProperty.Register<StudyText, (int First, int Count)?>(nameof(Highlight));

    private static readonly TimeSpan DoubleTapWait = TimeSpan.FromMilliseconds(230);

    private readonly List<(TextBlock Block, int Index, Segment Segment)> _words = [];
    private IDisposable? _tapWait;

    public StudyText() => ActualThemeVariantChanged += (_, _) => Rebuild();

    public ConvertedLine? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }

    public IReadOnlyList<WordReading?>? Readings { get => GetValue(ReadingsProperty); set => SetValue(ReadingsProperty, value); }

    public double TextSize { get => GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }

    public bool ShowChinese { get => GetValue(ShowChineseProperty); set => SetValue(ShowChineseProperty, value); }

    public bool ShowPinyin { get => GetValue(ShowPinyinProperty); set => SetValue(ShowPinyinProperty, value); }

    public bool ShowHanViet { get => GetValue(ShowHanVietProperty); set => SetValue(ShowHanVietProperty, value); }

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
        if (change.Property == HighlightProperty) ApplyHighlight();
        else if (change.Property == LineProperty || change.Property == ReadingsProperty || change.Property == TextSizeProperty
                 || change.Property == ShowChineseProperty || change.Property == ShowPinyinProperty || change.Property == ShowHanVietProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        _words.Clear();
        if (Line is not { IsSceneBreak: false } line || Readings is not { } readings) return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        IBrush pinyinBrush = new SolidColorBrush(Color.Parse(dark ? "#F0B75A" : "#8A5A00"));
        IBrush hanVietBrush = new SolidColorBrush(Color.Parse(dark ? "#5FD6C1" : "#0E7A6A"));
        IBrush nameBrush = new SolidColorBrush(Accent(dark ? "SystemAccentColorLight1" : "SystemAccentColorDark1"));
        IBrush? quiet = this.TryFindResource("HelmSecondaryText", ActualThemeVariant, out var dim) ? dim as IBrush : null;
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
            void Row(string value, double rowSize, IBrush? brush, FontFamily? font = null)
            {
                var run = new Run(value) { FontSize = rowSize };
                if (brush is not null) run.Foreground = brush;
                if (font is not null) run.FontFamily = font;
                block.Inlines!.Add(run);
                block.Inlines.Add(new LineBreak());
            }
            // No font named: the system picks its own Chinese font, as for the 中文 view (a named one may be missing).
            if (ShowChinese && reading.Chinese.Length > 0) Row(reading.Chinese, size * 1.2, null);
            if (ShowPinyin && reading.Pinyin.Length > 0) Row(reading.Pinyin, size * 0.64, pinyinBrush);
            if (ShowHanViet && reading.HanViet.Length > 0) Row(reading.HanViet, size * 0.64, hanVietBrush);
            // A grammar word with no Vietnamese of its own (的, 般) keeps a quiet dot, so its block stays a word.
            var vietnamese = new Run(prefix + (segment.Text.Length > 0 ? segment.Text : "·")) { FontSize = size };
            if (segment.Text.Length == 0) vietnamese.Foreground = quiet;
            else if (segment.Kind == SegmentKind.Name) vietnamese.Foreground = nameBrush;
            prefix = "";
            block.Inlines.Add(vietnamese);
            block.Tapped += OnTapped;
            block.DoubleTapped += OnDoubleTapped;
            _words.Add((block, i, segment));
            Children.Add(block);
            lastVietnamese = vietnamese;
        }
        if (prefix.Length > 0 && lastVietnamese is not null) lastVietnamese.Text += prefix;
        ApplyHighlight();
    }

    private Color Accent(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : Color.FromUInt32(0xFF0067C0);

    private void ApplyHighlight()
    {
        var range = Highlight;
        var fill = new SolidColorBrush(Accent("SystemAccentColor"), 0.38);
        foreach (var (block, index, _) in _words)
            // Never null: a transparent fill makes the whole block tappable, not only its letters.
            block.Background = range is { } r && index >= r.First && index < r.First + r.Count ? fill : Brushes.Transparent;
    }

    private (TextBlock Block, int Index, Segment Segment)? WordOf(object? sender) =>
        sender is TextBlock { Tag: int index } ? _words.FirstOrDefault(w => w.Index == index) : null;

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (WordOf(sender) is not { } word) return;
        e.Handled = true;
        _tapWait?.Dispose();
        _tapWait = DispatcherTimer.RunOnce(() => RaiseEvent(new WordTappedEventArgs(SegmentText.WordTappedEvent, word.Segment, word.Index)), DoubleTapWait);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        _tapWait?.Dispose();
        _tapWait = null;
        if (WordOf(sender) is not { } word) return;
        e.Handled = true;
        RaiseEvent(new WordTappedEventArgs(SegmentText.WordDoubleTappedEvent, word.Segment, word.Index));
    }
}
