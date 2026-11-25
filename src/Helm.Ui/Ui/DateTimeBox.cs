using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using Helm.Core.Text;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Core.Ui;

/// <summary>
/// One field for a day and a time, the same everywhere in Helm. A tap opens the picker: quick picks (Today, Tomorrow,
/// the weekend, next week; or Yesterday for things that happened), a month to tap a day in, the time (common hours or
/// any hour and minute), then Done. On a phone the picker is a sheet from the bottom; on a wider window a card in the
/// middle. Binds <see cref="Date"/> (the day; its time part is ignored) and <see cref="Time"/> separately, so view
/// models keep their date and time properties.
/// </summary>
public sealed class DateTimeBox : UserControl
{
    public static readonly StyledProperty<DateTime?> DateProperty =
        AvaloniaProperty.Register<DateTimeBox, DateTime?>(nameof(Date), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<TimeSpan?> TimeProperty =
        AvaloniaProperty.Register<DateTimeBox, TimeSpan?>(nameof(Time), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowTimeProperty = AvaloniaProperty.Register<DateTimeBox, bool>(nameof(ShowTime), true);

    public static readonly StyledProperty<DateLean> LeanProperty = AvaloniaProperty.Register<DateTimeBox, DateLean>(nameof(Lean));

    public static readonly StyledProperty<bool> AllowEmptyProperty = AvaloniaProperty.Register<DateTimeBox, bool>(nameof(AllowEmpty));

    public static readonly StyledProperty<string> PlaceholderTextProperty = AvaloniaProperty.Register<DateTimeBox, string>(nameof(PlaceholderText), "");

    /// <summary>Below this window width the picker is a bottom sheet (a phone); above it a card in the middle.</summary>
    private const double SheetBelow = 720;

    private static readonly TimeSpan[] CommonTimes = [new(9, 0, 0), new(12, 0, 0), new(17, 0, 0), new(20, 0, 0)];

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SymbolIcon _icon = new() { Symbol = Symbol.Calendar, FontSize = 18, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _field;
    private Control? _overlay;
    private DateTime _draftDate;
    private TimeSpan? _draftTime;
    private DateTime _month;
    private TextBlock? _summary;
    private TextBlock? _monthTitle;
    private UniformGrid? _days;
    private WrapPanel? _quick;
    private WrapPanel? _times;
    private ComboBox? _hour;
    private ComboBox? _minute;
    private bool _syncing;

    public DateTimeBox()
    {
        var field = _field = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 40,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _text } },
        };
        field.Click += (_, _) => Open();
        Content = field;
        ShowValue();
    }

    /// <summary>The day (its time part is ignored); null for none.</summary>
    public DateTime? Date
    {
        get => GetValue(DateProperty);
        set => SetValue(DateProperty, value);
    }

    /// <summary>The time of day; null for none (a day only).</summary>
    public TimeSpan? Time
    {
        get => GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    /// <summary>False for a day only (a deadline): no time.</summary>
    public bool ShowTime
    {
        get => GetValue(ShowTimeProperty);
        set => SetValue(ShowTimeProperty, value);
    }

    /// <summary>Past for things that happened (a payment), Future for things due: the quick picks follow.</summary>
    public DateLean Lean
    {
        get => GetValue(LeanProperty);
        set => SetValue(LeanProperty, value);
    }

    /// <summary>Whether the value can be cleared (Clear, and No time for an optional time).</summary>
    public bool AllowEmpty
    {
        get => GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    public string PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DateProperty || change.Property == TimeProperty || change.Property == ShowTimeProperty || change.Property == PlaceholderTextProperty)
            ShowValue();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Close();
        base.OnDetachedFromVisualTree(e);
    }

    private void ShowValue()
    {
        var text = DateTimeText.Format(Date is { } d ? DateOnly.FromDateTime(d) : null, ShowTime ? Time : null, DateOnly.FromDateTime(DateTime.Today));
        _text.Text = text.Length > 0 ? text : PlaceholderText;
        _text.Opacity = text.Length > 0 ? 1 : 0.6;
        _icon.Symbol = ShowTime ? Symbol.CalendarClock : Symbol.Calendar;
        // Screen readers hear what it is and what it says ("Due date: Tomorrow, 09:00").
        Avalonia.Automation.AutomationProperties.SetName(_field, text.Length > 0 && PlaceholderText.Length > 0 ? $"{PlaceholderText}: {text}" : _text.Text);
    }

    private static TimeSpan NowTime()
    {
        var now = DateTime.Now;
        return new TimeSpan(now.Hour, now.Minute, 0);
    }

    // ---- The picker ----------------------------------------------------------------------------------------------

    private void Open()
    {
        if (_overlay is not null || OverlayLayer.GetOverlayLayer(this) is not { } layer || TopLevel.GetTopLevel(this) is not { } top) return;
        _draftDate = (Date ?? DateTime.Today).Date;
        _draftTime = Time ?? (ShowTime && !AllowEmpty ? NowTime() : null);
        _month = new DateTime(_draftDate.Year, _draftDate.Month, 1);
        // The screen it opens over: the window, or a stand-in screen (the Gallery's phone frame, class "screen").
        Control screen = this.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Classes.Contains("screen")) ?? top;
        var at = screen.TranslatePoint(default, top) ?? default;
        var size = screen.Bounds.Size;
        var phone = size.Width < SheetBelow;

        var body = new StackPanel { Spacing = 16 };
        if (phone)
            body.Children.Add(new Border
            {
                Width = 36, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Colors.Gray, 0.5),
            });

        // What it says now, in words.
        _summary = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var head = new StackPanel { Spacing = 2 };
        if (PlaceholderText.Length > 0) head.Children.Add(new TextBlock { Text = PlaceholderText.ToUpper(CultureInfo.CurrentCulture), Classes = { "eyebrow" } });
        head.Children.Add(_summary);
        body.Children.Add(head);

        // Quick picks.
        _quick = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        body.Children.Add(_quick);

        // The month.
        _monthTitle = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var previous = IconChip(Symbol.ChevronLeft, "Previous month", () => ShowMonth(-1));
        var next = IconChip(Symbol.ChevronRight, "Next month", () => ShowMonth(1));
        var monthBar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,8,Auto") };
        monthBar.Children.Add(_monthTitle);
        Grid.SetColumn(previous, 1);
        Grid.SetColumn(next, 3);
        monthBar.Children.Add(previous);
        monthBar.Children.Add(next);
        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 4, 0, 0) };
        var culture = CultureInfo.CurrentCulture;
        var firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
            weekdays.Children.Add(new TextBlock
            {
                Text = culture.DateTimeFormat.ShortestDayNames[(firstDay + i) % 7],
                Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
            });
        _days = new UniformGrid { Columns = 7, Rows = 6 };
        body.Children.Add(new StackPanel { Spacing = 6, Children = { monthBar, weekdays, _days } });

        // The time.
        if (ShowTime)
        {
            _times = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            _hour = new ComboBox { Classes = { "glass" }, ItemsSource = Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.InvariantCulture)).ToList(), MinWidth = 84, PlaceholderText = "Hour" };
            _minute = new ComboBox { Classes = { "glass" }, ItemsSource = Enumerable.Range(0, 12).Select(m => (m * 5).ToString("00", CultureInfo.InvariantCulture)).ToList(), MinWidth = 84, PlaceholderText = "Min" };
            Avalonia.Automation.AutomationProperties.SetName(_hour, "Hour");
            Avalonia.Automation.AutomationProperties.SetName(_minute, "Minute");
            _hour.SelectionChanged += (_, _) => PickTime();
            _minute.SelectionChanged += (_, _) => PickTime();
            var exact = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { _hour, new TextBlock { Text = ":", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _minute },
            };
            body.Children.Add(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Time", FontWeight = FontWeight.SemiBold }, _times, exact } });
        }

        // Clear · Cancel · Done
        var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,8,Auto") };
        if (AllowEmpty)
            footer.Children.Add(TextButton("Clear", "ghost", () =>
            {
                Date = null;
                Time = null;
                Close();
            }));
        var cancel = TextButton("Cancel", "ghost", Close);
        var done = TextButton("Done", "pill", () =>
        {
            Date = _draftDate;
            if (ShowTime) Time = _draftTime;
            Close();
        });
        done.MinWidth = 96;
        Grid.SetColumn(cancel, 2);
        Grid.SetColumn(done, 4);
        footer.Children.Add(cancel);
        footer.Children.Add(done);
        body.Children.Add(footer);

        var card = new Border
        {
            Classes = { "sheet" },
            Padding = phone ? new Thickness(18, 10, 18, 24) : new Thickness(22),
            Width = phone ? double.NaN : 400,
            VerticalAlignment = phone ? VerticalAlignment.Bottom : VerticalAlignment.Center,
            HorizontalAlignment = phone ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            Child = new ScrollViewer { Content = body, MaxHeight = size.Height - 48, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        if (phone) card.CornerRadius = new CornerRadius(20, 20, 0, 0);
        // A tap on the card itself must not reach the scrim.
        card.PointerPressed += (_, e) => e.Handled = true;
        var scrim = new Grid { Width = size.Width, Height = size.Height, Children = { card } };
        scrim.Bind(Panel.BackgroundProperty, this.GetResourceObservable("HelmScrim"));
        scrim.PointerPressed += (_, _) => Close();
        scrim.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        // The overlay layer is outside the page: keep the page's light/dark (it may differ from the window's, in the Gallery).
        var overlay = new ThemeVariantScope { RequestedThemeVariant = ActualThemeVariant, Child = scrim };
        Canvas.SetLeft(overlay, at.X);
        Canvas.SetTop(overlay, at.Y);
        _overlay = overlay;
        layer.Children.Add(overlay);
        Refresh();
        // A sheet slides up from the bottom; the card rises a little.
        Motion.Enter(card, phone ? 60 : 12);
    }

    private void Close()
    {
        if (_overlay is null) return;
        if (_overlay.Parent is Panel panel) panel.Children.Remove(_overlay);
        _overlay = null;
        _summary = _monthTitle = null;
        _days = null;
        _quick = _times = null;
        _hour = _minute = null;
    }

    private static Button TextButton(string text, string kind, Action click)
    {
        var b = new Button { Content = text, Classes = { kind }, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button IconChip(Symbol symbol, string name, Action click)
    {
        var b = new Button { Classes = { "chip" }, Content = new SymbolIcon { Symbol = symbol, FontSize = 14 } };
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    private Button Chip(string text, bool selected, Action click)
    {
        var b = new Button { Classes = { "chip" }, Content = text };
        if (selected) b.Classes.Add("selected");
        b.Click += (_, _) =>
        {
            click();
            Refresh();
        };
        return b;
    }

    private void ShowMonth(int step)
    {
        _month = _month.AddMonths(step);
        Refresh();
    }

    /// <summary>Redraws everything that follows the draft: the summary, the chips, the month and the time.</summary>
    private void Refresh()
    {
        if (_summary is null || _quick is null || _days is null || _monthTitle is null) return;
        var culture = CultureInfo.CurrentCulture;
        var today = DateTime.Today;
        _summary.Text = DateTimeText.Format(DateOnly.FromDateTime(_draftDate), ShowTime ? _draftTime : null, DateOnly.FromDateTime(today));

        _quick.Children.Clear();
        foreach (var (label, day) in QuickDays(today))
            _quick.Children.Add(Chip(label, day == _draftDate, () =>
            {
                _draftDate = day;
                _month = new DateTime(day.Year, day.Month, 1);
            }));

        _monthTitle.Text = _month.ToString("MMMM yyyy", culture);
        _days.Children.Clear();
        var offset = ((int)_month.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var first = _month.AddDays(-offset);
        for (var i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            var cell = new Button { Classes = { "day" }, Content = day.Day.ToString(culture) };
            if (day.Month != _month.Month) cell.Classes.Add("out");
            if (day == today) cell.Classes.Add("today");
            if (day == _draftDate) cell.Classes.Add("selected");
            Avalonia.Automation.AutomationProperties.SetName(cell, day.ToString("D", culture));
            cell.Click += (_, _) =>
            {
                _draftDate = day;
                if (day.Month != _month.Month) _month = new DateTime(day.Year, day.Month, 1);
                Refresh();
            };
            _days.Children.Add(cell);
        }

        if (_times is not null && _hour is not null && _minute is not null)
        {
            _times.Children.Clear();
            if (AllowEmpty) _times.Children.Add(Chip("No time", _draftTime is null, () => _draftTime = null));
            if (Lean == DateLean.Past) _times.Children.Add(Chip("Now", false, () => _draftTime = NowTime()));
            foreach (var t in CommonTimes)
                _times.Children.Add(Chip(DateTimeText.Clock(t), _draftTime == t, () => _draftTime = t));
            _syncing = true;
            _hour.SelectedIndex = _draftTime?.Hours ?? -1;
            _minute.SelectedIndex = _draftTime is { } dt ? dt.Minutes / 5 : -1;
            _syncing = false;
        }
    }

    /// <summary>The quick picks: days ahead for things due, days back for things that happened.</summary>
    private IEnumerable<(string Label, DateTime Day)> QuickDays(DateTime today)
    {
        if (Lean == DateLean.Past)
        {
            yield return ("Today", today);
            yield return ("Yesterday", today.AddDays(-1));
            yield return ("2 days ago", today.AddDays(-2));
            yield break;
        }
        yield return ("Today", today);
        yield return ("Tomorrow", today.AddDays(1));
        var saturday = today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7);
        if (saturday > today.AddDays(1)) yield return ("This weekend", saturday);
        var monday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7 is 0 ? 7 : ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7);
        yield return ("Next week", monday);
    }

    private void PickTime()
    {
        if (_syncing || _hour is null || _minute is null || _hour.SelectedIndex < 0 && _minute.SelectedIndex < 0) return;
        _draftTime = new TimeSpan(Math.Max(0, _hour.SelectedIndex), Math.Max(0, _minute.SelectedIndex) * 5, 0);
        Refresh();
    }
}
