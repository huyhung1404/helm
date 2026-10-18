using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Lens;

namespace Helm.Modules.NovelReader.Lens;

/// <summary>
/// The lens's page over the reading app: each paragraph in study form (Chinese, pinyin, Hán Việt, Vietnamese) to scroll
/// through, a status line on top and the buttons at the bottom (back, read again, text size, Next, close). Colours come
/// from Helm's theme, as on its own pages.
/// </summary>
internal sealed class LensPanel : UserControl
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Width = 64, Height = 4, MinWidth = 0, IsVisible = false, Margin = new(8, 0, 0, 0) };
    private readonly StackPanel _rows = new() { Spacing = 4, Margin = new(16, 8, 16, 24) };
    private readonly ScrollViewer _scroll;
    private IReadOnlyList<LensRow> _shown = [];
    private (bool From, bool On) _continues;
    private double _textSize = 18;

    // ---- The word being fixed ----
    private readonly Border _editor;
    private readonly TextBox _chinese = new() { FontSize = 20, MinWidth = 120 };
    private readonly TextBlock _hanViet = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _vietnamese = new() { PlaceholderText = "Vietnamese" };
    private readonly CheckBox _forNovel = new();
    private string _source = "";
    private int _start, _length;

    /// <summary>Names and meanings: the Hán Việt to offer for a word, and saving one (then the page is converted again).</summary>
    public Func<string, string>? Suggest { get; init; }

    public Func<string, string, EntryKind, bool, Task>? SaveEntry { get; init; }

    /// <summary>The novel the names go to, or null (then they can only be saved for every novel).</summary>
    public Func<string?>? NovelTitle { get; init; }

    /// <summary>Whether something was saved for a word, and removing it (then the page is converted again).</summary>
    public Func<string, bool>? HasEntry { get; init; }

    public Func<string, Task>? RemoveEntry { get; init; }

    private Button? _remove;

    public LensPanel(Func<Task> back, Func<Task> readAgain, Func<Task> next, Action close, Action<double> textSizeChanged)
    {
        _editor = EditorView();
        Bind(BackgroundProperty, this.GetResourceObservable("HelmPageBackground"));
        _scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var top = new DockPanel { Margin = new(16, 10, 16, 6) };
        DockPanel.SetDock(_progress, Dock.Right);
        top.Children.Add(_progress);
        top.Children.Add(_status);
        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("HelmSecondaryText"));

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto,Auto,*,Auto"), Margin = new(8, 6, 8, 10) };
        void Add(Control c, int column)
        {
            Grid.SetColumn(c, column);
            bar.Children.Add(c);
        }
        Add(BarButton("‹ Back", back), 0);
        Add(BarButton("Read again", readAgain), 1);
        Add(BarButton("A−", () => Resize(-2, textSizeChanged), 44), 2);
        Add(BarButton("A+", () => Resize(+2, textSizeChanged), 44), 3);
        var nextButton = BarButton("Next ›", next);
        nextButton.Classes.Add("accent");
        Add(nextButton, 4);
        Add(BarButton("✕", () =>
        {
            close();
            return Task.CompletedTask;
        }, 44), 5);
        var barHost = new Border { Child = bar };
        barHost.Bind(Border.BackgroundProperty, this.GetResourceObservable("HelmBarBackground"));

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(barHost, Dock.Bottom);
        DockPanel.SetDock(_editor, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(barHost);
        root.Children.Add(_editor);
        root.Children.Add(_scroll);
        Content = root;
    }

    /// <summary>The sheet that fixes a word: its Chinese (widened a character at a time), the Vietnamese, where it is saved.</summary>
    private Border EditorView()
    {
        var left = SmallButton("◀", () => Widen(-1));
        var right = SmallButton("▶", () => Widen(+1));
        var word = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        word.Children.Add(left);
        word.Children.Add(right);
        word.Children.Add(_chinese);
        _chinese.TextChanged += (_, _) =>
        {
            var chinese = _chinese.Text ?? "";
            _hanViet.Text = (Suggest?.Invoke(chinese) ?? "") is { Length: > 0 } hv ? "Hán Việt: " + hv : "";
            if (_remove is not null) _remove.IsVisible = HasEntry?.Invoke(chinese) == true;
        };
        _hanViet.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("HelmSecondaryText"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var name = SmallButton("Save name", () => SaveAsync(EntryKind.Name));
        name.Classes.Add("accent");
        _remove = SmallButton("Remove", async () =>
        {
            if (RemoveEntry is null || string.IsNullOrWhiteSpace(_chinese.Text)) return;
            await RemoveEntry(_chinese.Text);
            CloseEditor();
        });
        buttons.Children.Add(_remove);
        buttons.Children.Add(SmallButton("Cancel", CloseEditor));
        buttons.Children.Add(SmallButton("Save meaning", () => SaveAsync(EntryKind.Phrase)));
        buttons.Children.Add(name);

        var sheet = new Border
        {
            Padding = new(16, 12),
            IsVisible = false,
            Child = new StackPanel { Spacing = 8, Children = { word, _hanViet, _vietnamese, _forNovel, buttons } },
        };
        sheet.Bind(Border.BackgroundProperty, this.GetResourceObservable("HelmCardBackground"));
        sheet.Bind(Border.BorderBrushProperty, this.GetResourceObservable("HelmCardStroke"));
        sheet.BorderThickness = new(0, 1, 0, 0);
        return sheet;
    }

    private static Button SmallButton(string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 40, MinWidth = 44, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button SmallButton(string text, Func<Task> action)
    {
        var button = SmallButton(text, () => { });
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>A tapped word opens the sheet with its Chinese, offering the name it would have.</summary>
    private void OpenEditor(ConvertedLine line, Segment segment)
    {
        (_source, _start, _length) = (line.Source, segment.Start, segment.Length);
        _chinese.Text = line.SourceOf(segment);
        _vietnamese.Text = segment.Kind == SegmentKind.Name ? segment.Text : Suggest?.Invoke(_chinese.Text) ?? "";
        var title = NovelTitle?.Invoke();
        // A text block, not a string: a string content would take "_" in the title as an access key.
        _forNovel.Content = new TextBlock
        {
            Text = title is null ? "Only for this novel (choose one in Novel Reader settings)" : $"Only for {title}",
            TextWrapping = TextWrapping.Wrap,
        };
        _forNovel.IsEnabled = title is not null;
        _forNovel.IsChecked = title is not null;
        _editor.IsVisible = true;
    }

    /// <summary>Takes in the next character on one side (OCR or the dictionary cut a name in pieces).</summary>
    private void Widen(int side)
    {
        if (side < 0 && _start > 0 && Text.ChineseText.IsHan(_source[_start - 1])) (_start, _length) = (_start - 1, _length + 1);
        else if (side > 0 && _start + _length < _source.Length && Text.ChineseText.IsHan(_source[_start + _length])) _length++;
        else return;
        _chinese.Text = _source.Substring(_start, _length);
        _vietnamese.Text = Suggest?.Invoke(_chinese.Text) ?? _vietnamese.Text;
    }

    private async Task SaveAsync(EntryKind kind)
    {
        if (SaveEntry is null || string.IsNullOrWhiteSpace(_chinese.Text) || string.IsNullOrWhiteSpace(_vietnamese.Text)) return;
        await SaveEntry(_chinese.Text, _vietnamese.Text, kind, _forNovel.IsChecked == true);
        CloseEditor();
    }

    private void CloseEditor() => _editor.IsVisible = false;

    public double TextSize
    {
        get => _textSize;
        set
        {
            _textSize = Math.Clamp(value, 12, 36);
            Render();
        }
    }

    private Task Resize(double by, Action<double> changed)
    {
        TextSize = _textSize + by;
        changed(_textSize);
        return Task.CompletedTask;
    }

    private static Button BarButton(string text, Func<Task> action, double minWidth = 0)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 44,
            MinWidth = minWidth,
            Margin = new(3, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>Busy (reading the page): a moving bar and a status line; the page shown stays until the new one is there.</summary>
    public void SetBusy(bool busy, string? status = null)
    {
        _progress.IsVisible = busy;
        if (status is not null) _status.Text = status;
    }

    public void SetStatus(string status) => _status.Text = status;

    /// <summary>Shows a page; <paramref name="keepPlace"/> keeps the scroll position (the same page converted again).</summary>
    public void Show(IReadOnlyList<LensRow> rows, bool continuesFromPrevious, bool continuesOnNext, bool keepPlace = false)
    {
        var offset = _scroll.Offset;
        _shown = rows;
        _continues = (continuesFromPrevious, continuesOnNext);
        Render();
        _scroll.Offset = keepPlace ? offset : default;
        if (!keepPlace) CloseEditor();
    }

    public void Clear()
    {
        _shown = [];
        _rows.Children.Clear();
        CloseEditor();
    }

    private void Render()
    {
        _rows.Children.Clear();
        if (_continues.From && _shown.Count > 0) _rows.Children.Add(Note("… from the page before"));
        foreach (var row in _shown)
        {
            var study = new StudyText
            {
                Line = row.Line,
                Readings = row.Readings,
                // The title stands out by size (a study block has no weight of its own).
                TextSize = row.IsHeading ? _textSize * 1.2 : _textSize,
                Margin = new Thickness(0, row.IsHeading ? 8 : 0, 0, row.IsHeading ? 12 : 6),
            };
            var line = row.Line;
            study.AddHandler(SegmentText.WordTappedEvent, (_, e) => OpenEditor(line, e.Segment));
            _rows.Children.Add(study);
        }
        if (_continues.On && _shown.Count > 0) _rows.Children.Add(Note("goes on on the next page …"));
    }

    private TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, FontSize = 12, FontStyle = FontStyle.Italic, Margin = new(0, 2, 0, 6) };
        note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("HelmSecondaryText"));
        return note;
    }
}
