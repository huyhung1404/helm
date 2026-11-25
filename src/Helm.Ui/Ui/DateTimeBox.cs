using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Helm.Core.Text;
using Calendar = Avalonia.Controls.Calendar;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Core.Ui;

/// <summary>
/// One field for a day and a time, the same everywhere in Helm on the phone. A tap opens one sheet from the bottom: a
/// strip of days to swipe (with a calendar for a day further away), hour and minute columns, and Now / None / Done.
/// Binds <see cref="Date"/> (the day; its time part is ignored) and <see cref="Time"/> separately, so view models keep
/// their date and time properties. The Windows app has the same control (typed text, chips and a panel).
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

    private const int PastDays = 60;
    private const int FutureDays = 120;

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SymbolIcon _icon = new() { Symbol = Symbol.Calendar, FontSize = 18, Margin = new Thickness(0, 0, 10, 0) };
    private Control? _sheet;
    private DateTime _draftDate;
    private TimeSpan? _draftTime;
    private StackPanel? _strip;
    private ScrollViewer? _stripScroll;
    private Calendar? _calendar;
    private ListBox? _hours;
    private ListBox? _minutes;
    private bool _syncing;

    public DateTimeBox()
    {
        var field = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 44,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _text } },
        };
        field.Click += (_, _) => OpenSheet();
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

    /// <summary>False for a day only (a deadline): no time columns.</summary>
    public bool ShowTime
    {
        get => GetValue(ShowTimeProperty);
        set => SetValue(ShowTimeProperty, value);
    }

    /// <summary>Past for things that happened (a payment), Future for things due: the strip of days follows.</summary>
    public DateLean Lean
    {
        get => GetValue(LeanProperty);
        set => SetValue(LeanProperty, value);
    }

    /// <summary>Whether the value can be cleared (None, and No time for an optional time).</summary>
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
        CloseSheet();
        base.OnDetachedFromVisualTree(e);
    }

    private void ShowValue()
    {
        var text = DateTimeText.Format(Date is { } d ? DateOnly.FromDateTime(d) : null, ShowTime ? Time : null, DateOnly.FromDateTime(DateTime.Today));
        _text.Text = text.Length > 0 ? text : PlaceholderText;
        _text.Opacity = text.Length > 0 ? 1 : 0.6;
        _icon.Symbol = ShowTime ? Symbol.CalendarClock : Symbol.Calendar;
    }

    private static TimeSpan NowTime()
    {
        var now = DateTime.Now;
        return new TimeSpan(now.Hour, now.Minute, 0);
    }

    // ---- The sheet -----------------------------------------------------------------------------------------------

    private void OpenSheet()
    {
        if (_sheet is not null || OverlayLayer.GetOverlayLayer(this) is not { } layer || TopLevel.GetTopLevel(this) is not { } top) return;
        _draftDate = (Date ?? DateTime.Today).Date;
        _draftTime = Time ?? (ShowTime && !AllowEmpty ? NowTime() : null);

        var body = new StackPanel { Spacing = 12 };
        var grab = new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center };
        grab.Bind(Border.BackgroundProperty, this.GetResourceObservable("HelmCardStroke"));
        body.Children.Add(grab);

        // Days: a strip to swipe, and a calendar for a day further away.
        _strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _stripScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _strip };
        body.Children.Add(_stripScroll);
        _calendar = new Calendar { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, SelectionMode = CalendarSelectionMode.SingleDate };
        _calendar.SelectedDatesChanged += (_, _) =>
        {
            if (_syncing || _calendar.SelectedDate is not { } d) return;
            _draftDate = d.Date;
            BuildStrip();
        };
        var other = new Button { Content = "Another day…", HorizontalAlignment = HorizontalAlignment.Left };
        other.Click += (_, _) =>
        {
            _calendar.IsVisible = !_calendar.IsVisible;
            _syncing = true;
            _calendar.SelectedDate = _draftDate;
            _calendar.DisplayDate = _draftDate;
            _syncing = false;
        };
        body.Children.Add(other);
        body.Children.Add(_calendar);

        // Time: hour and minute columns.
        if (ShowTime)
        {
            _hours = Column(Enumerable.Range(0, 24));
            _minutes = Column(Enumerable.Range(0, 60));
            _hours.SelectionChanged += (_, _) => PickTime();
            _minutes.SelectionChanged += (_, _) => PickTime();
            var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            times.Children.Add(_hours);
            times.Children.Add(new TextBlock { Text = ":", FontSize = 22, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            times.Children.Add(_minutes);
            body.Children.Add(times);
            if (AllowEmpty)
            {
                var noTime = new CheckBox { Content = "No time", IsChecked = _draftTime is null, HorizontalAlignment = HorizontalAlignment.Center };
                noTime.IsCheckedChanged += (_, _) =>
                {
                    _draftTime = noTime.IsChecked == true ? null : _draftTime ?? new TimeSpan(9, 0, 0);
                    SyncColumns();
                };
                body.Children.Add(noTime);
            }
        }

        // Now / None / Done
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var nowLabel = Lean == DateLean.Past && ShowTime ? "Now" : "Today";
        buttons.Children.Add(SheetButton(nowLabel, false, () =>
        {
            _draftDate = DateTime.Today;
            if (Lean == DateLean.Past && ShowTime) _draftTime = NowTime();
            BuildStrip();
            SyncColumns();
        }));
        if (AllowEmpty)
            buttons.Children.Add(SheetButton("None", false, () =>
            {
                Date = null;
                Time = null;
                CloseSheet();
            }));
        buttons.Children.Add(SheetButton("Done", true, () =>
        {
            Date = _draftDate;
            if (ShowTime) Time = _draftTime;
            CloseSheet();
        }));
        body.Children.Add(buttons);

        var sheet = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(16, 16, 0, 0),
            Padding = new Thickness(16, 10, 16, 28),
            Child = body,
        };
        sheet.Bind(Border.BackgroundProperty, this.GetResourceObservable("HelmCardBackground"));
        // A tap on the sheet itself must not reach the scrim.
        sheet.PointerPressed += (_, e) => e.Handled = true;
        var scrim = new Grid
        {
            Width = top.ClientSize.Width,
            Height = top.ClientSize.Height,
            Children = { sheet },
        };
        scrim.Bind(Panel.BackgroundProperty, this.GetResourceObservable("HelmScrim"));
        scrim.PointerPressed += (_, _) => CloseSheet();
        _sheet = scrim;
        layer.Children.Add(scrim);
        BuildStrip();
        SyncColumns();
    }

    private void CloseSheet()
    {
        if (_sheet is null) return;
        if (_sheet.Parent is Panel panel) panel.Children.Remove(_sheet);
        _sheet = null;
        _strip = null;
        _calendar = null;
        _hours = _minutes = null;
    }

    private static Button SheetButton(string text, bool primary, Action click)
    {
        var b = new Button { Content = text, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        if (primary) b.Classes.Add("accent");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The days around the chosen one, newest on the right for the past and oldest on the left for the future.</summary>
    private void BuildStrip()
    {
        if (_strip is null) return;
        _strip.Children.Clear();
        var today = DateTime.Today;
        var first = Lean == DateLean.Past ? today.AddDays(-PastDays) : today;
        var last = Lean == DateLean.Past ? today : today.AddDays(FutureDays);
        if (_draftDate < first) first = _draftDate;
        if (_draftDate > last) last = _draftDate;
        Button? selected = null;
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            var day = d;
            var culture = CultureInfo.CurrentCulture;
            var top = day == today ? "Today" : day == today.AddDays(-1) ? "Yest." : day == today.AddDays(1) ? "Tmrw" : day.ToString("ddd", culture);
            var chip = new Button
            {
                Width = 58,
                Padding = new Thickness(0, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = top, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = day.Day.ToString(culture), FontSize = 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = day.ToString("MMM", culture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            if (day == _draftDate)
            {
                chip.Classes.Add("accent");
                selected = chip;
            }
            chip.Click += (_, _) =>
            {
                _draftDate = day;
                BuildStrip();
            };
            _strip.Children.Add(chip);
        }
        if (selected is not null) Avalonia.Threading.Dispatcher.UIThread.Post(() => selected.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private static ListBox Column(IEnumerable<int> values) => new()
    {
        Width = 76,
        Height = 176,
        ItemsSource = values.Select(v => v.ToString("00", CultureInfo.InvariantCulture)).ToList(),
    };

    private void PickTime()
    {
        if (_syncing || _hours is null || _minutes is null) return;
        var h = Math.Max(0, _hours.SelectedIndex);
        var m = Math.Max(0, _minutes.SelectedIndex);
        if (_hours.SelectedIndex < 0 && _minutes.SelectedIndex < 0) return;
        _draftTime = new TimeSpan(h, m, 0);
        SyncColumns();
    }

    private void SyncColumns()
    {
        if (_hours is null || _minutes is null) return;
        _syncing = true;
        _hours.SelectedIndex = _draftTime?.Hours ?? -1;
        _minutes.SelectedIndex = _draftTime?.Minutes ?? -1;
        _hours.Opacity = _minutes.Opacity = _draftTime is null ? 0.5 : 1;
        _syncing = false;
        var hours = _hours;
        var minutes = _minutes;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (hours.SelectedIndex >= 0) hours.ScrollIntoView(hours.SelectedIndex);
            if (minutes.SelectedIndex >= 0) minutes.ScrollIntoView(minutes.SelectedIndex);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
