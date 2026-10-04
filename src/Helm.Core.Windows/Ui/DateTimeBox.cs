using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Helm.Core.Text;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Calendar = System.Windows.Controls.Calendar;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Helm.Core.Ui;

/// <summary>
/// One box for a day and a time, the same everywhere in Helm. Type it ("hôm qua 18h45", "mai 9h", "3/10 14:30",
/// "thứ 6") and the line under the box shows what Helm read before Enter or leaving the box saves it; or tap a
/// shortcut chip above it; or open the panel (the calendar button) with the month, a column of times and the exact
/// hour and minute. Binds <see cref="Date"/> (the day; its time part is ignored) and <see cref="Time"/> separately,
/// so view models keep their date and time properties.
/// </summary>
public sealed class DateTimeBox : UserControl
{
    public static readonly DependencyProperty DateProperty = DependencyProperty.Register(
        nameof(Date), typeof(DateTime?), typeof(DateTimeBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((DateTimeBox)d).ShowValue()));

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(TimeSpan?), typeof(DateTimeBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((DateTimeBox)d).ShowValue()));

    public static readonly DependencyProperty ShowTimeProperty = DependencyProperty.Register(
        nameof(ShowTime), typeof(bool), typeof(DateTimeBox), new PropertyMetadata(true, (d, _) => ((DateTimeBox)d).Rebuild()));

    public static readonly DependencyProperty LeanProperty = DependencyProperty.Register(
        nameof(Lean), typeof(DateLean), typeof(DateTimeBox), new PropertyMetadata(DateLean.Past, (d, _) => ((DateTimeBox)d).Rebuild()));

    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
        nameof(AllowEmpty), typeof(bool), typeof(DateTimeBox), new PropertyMetadata(false, (d, _) => ((DateTimeBox)d).Rebuild()));

    public static readonly DependencyProperty ShowChipsProperty = DependencyProperty.Register(
        nameof(ShowChips), typeof(bool), typeof(DateTimeBox), new PropertyMetadata(true, (d, _) => ((DateTimeBox)d).Rebuild()));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(DateTimeBox), new PropertyMetadata("", (d, e) => ((DateTimeBox)d)._box.PlaceholderText = e.NewValue as string ?? ""));

    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBox _box = new() { MinWidth = 200 };
    private readonly TextBlock _readout = new() { FontSize = 12, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Popup _popup;
    private readonly Calendar _calendar = new() { SelectionMode = CalendarSelectionMode.SingleDate };
    private readonly ListBox _times = new() { Width = 96, Height = 290, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBox _hour = new() { Width = 56, MaxLength = 2, TextAlignment = TextAlignment.Center };
    private readonly TextBox _minute = new() { Width = 56, MaxLength = 2, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _exact = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _panelButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private Border _frame = null!;
    private bool _showing;
    private bool _syncing;
    private bool _dirty;

    public DateTimeBox()
    {
        _readout.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _box.TextChanged += (_, _) => OnTyped();
        _box.KeyDown += OnBoxKey;
        _box.GotKeyboardFocus += (_, _) => _box.Dispatcher.BeginInvoke(_box.SelectAll);
        _box.LostKeyboardFocus += (_, _) => { if (!_popup!.IsOpen) Commit(); };

        // One frame holds it all: the box (borderless) with the calendar button inside it, the chips, the readout.
        _box.BorderThickness = new Thickness(0);
        _box.Background = System.Windows.Media.Brushes.Transparent;
        _box.ClearButtonEnabled = false;
        _box.Icon = new SymbolIcon(SymbolRegular.CalendarClock24);
        var open = new Button
        {
            Icon = new SymbolIcon(SymbolRegular.ChevronDown24),
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Pick on a calendar",
        };
        AutomationPropertiesName(open, "Pick on a calendar");
        open.BorderThickness = new Thickness(0);
        open.Background = System.Windows.Media.Brushes.Transparent;
        open.Click += (_, _) => OpenPanel();

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_box);
        Grid.SetColumn(open, 1);
        row.Children.Add(open);

        _chips.Margin = new Thickness(8, 2, 8, 4);
        _readout.Margin = new Thickness(10, 0, 8, 6);
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(_chips);
        stack.Children.Add(_readout);
        _frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack };
        _frame.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        ShowFocus(false);
        IsKeyboardFocusWithinChanged += (_, e) => ShowFocus((bool)e.NewValue);
        Content = _frame;

        // The panel: the month, the times, the exact hour and minute, and its buttons.
        _calendar.SelectedDatesChanged += (_, _) =>
        {
            if (_syncing || _calendar.SelectedDate is not { } day) return;
            Set(day, Time);
        };
        for (var t = 0; t < 24 * 60; t += 15) _times.Items.Add(DateTimeText.Clock(TimeSpan.FromMinutes(t)));
        _times.SelectionChanged += (_, _) =>
        {
            if (_syncing || _times.SelectedItem is not string s || !TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t)) return;
            Set(Date ?? Today(), t);
        };
        _hour.LostFocus += (_, _) => ApplyExact();
        _minute.LostFocus += (_, _) => ApplyExact();
        _hour.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyExact(); };
        _minute.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyExact(); };
        var exactLabel = new TextBlock { Text = "Exact", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        exactLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _exact.Children.Add(exactLabel);
        _exact.Children.Add(_hour);
        _exact.Children.Add(new TextBlock { Text = ":", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        _exact.Children.Add(_minute);

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_calendar);
        top.Children.Add(_times);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_panelButtons, Dock.Right);
        bottom.Children.Add(_panelButtons);
        bottom.Children.Add(_exact);
        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        var border = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        _popup = new Popup { Child = border, PlacementTarget = _frame, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        _popup.Closed += (_, _) => ShowValue();

        Rebuild();
    }

    /// <summary>The day (its time part is ignored); null for none.</summary>
    public DateTime? Date
    {
        get => (DateTime?)GetValue(DateProperty);
        set => SetValue(DateProperty, value);
    }

    /// <summary>The time of day; null for none (a day only).</summary>
    public TimeSpan? Time
    {
        get => (TimeSpan?)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    /// <summary>False for a day only (a deadline): no times, and the text never asks for one.</summary>
    public bool ShowTime
    {
        get => (bool)GetValue(ShowTimeProperty);
        set => SetValue(ShowTimeProperty, value);
    }

    /// <summary>Past for things that happened (a payment), Future for things due: the chips and weekday names follow.</summary>
    public DateLean Lean
    {
        get => (DateLean)GetValue(LeanProperty);
        set => SetValue(LeanProperty, value);
    }

    /// <summary>Whether the value can be cleared ("None", or an empty box).</summary>
    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    public bool ShowChips
    {
        get => (bool)GetValue(ShowChipsProperty);
        set => SetValue(ShowChipsProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>The frame's edge: the accent while the box is being used, a hairline otherwise.</summary>
    private void ShowFocus(bool focused) =>
        _frame.SetResourceReference(Border.BorderBrushProperty, focused ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");

    private static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static DateTime Today() => DateTime.Today;

    private static TimeSpan NowTime()
    {
        var now = DateTime.Now;
        return new TimeSpan(now.Hour, now.Minute, 0);
    }

    // ---- The value -----------------------------------------------------------------------------------------------

    private void Set(DateTime? day, TimeSpan? time)
    {
        Date = day?.Date;
        if (ShowTime) Time = day is null ? null : time;
        ShowValue();
    }

    /// <summary>The value as a phrase in the box (unless the user is typing in it).</summary>
    private void ShowValue()
    {
        if (_dirty && _box.IsKeyboardFocusWithin) return;
        _showing = true;
        _box.Text = DateTimeText.Format(Date is { } d ? DateOnly.FromDateTime(d) : null, ShowTime ? Time : null, DateOnly.FromDateTime(Today()));
        _showing = false;
        _dirty = false;
        _readout.Visibility = Visibility.Collapsed;
        if (_popup?.IsOpen == true) SyncPanel();
    }

    // ---- Typing --------------------------------------------------------------------------------------------------

    private void OnTyped()
    {
        if (_showing) return;
        _dirty = true;
        var text = _box.Text;
        string readout;
        if (string.IsNullOrWhiteSpace(text))
            readout = AllowEmpty ? "No date." : "Type a day or a time, e.g. hôm qua 18h45.";
        else if (DateTimeText.TryParse(text, DateTime.Now, Lean, out var day, out var time) && day is { } d)
            readout = "Helm reads: " + DateTimeText.Describe(d, ShowTime ? time ?? Time : null);
        else
            readout = ShowTime
                ? (Lean == DateLean.Past ? "Not understood. Try hôm qua 18h45, 3/10 9h or 14:30." : "Not understood. Try mai 9h, thứ 6 or 15/10 14:00.")
                : "Not understood. Try 15/10, thứ 6 or 2 tuần nữa.";
        _readout.Text = readout;
        _readout.Visibility = Visibility.Visible;
    }

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _dirty)
        {
            _dirty = false;
            ShowValue();
            e.Handled = true;
        }
    }

    /// <summary>Saves what was typed when it reads; otherwise puts the saved value back and says why.</summary>
    private void Commit()
    {
        if (!_dirty) return;
        var text = _box.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _dirty = false;
            if (AllowEmpty) Set(null, null);
            else ShowValue();
            return;
        }
        if (DateTimeText.TryParse(text, DateTime.Now, Lean, out var day, out var time) && day is { } d)
        {
            _dirty = false;
            Set(d.ToDateTime(TimeOnly.MinValue), time ?? Time);
            return;
        }
        _dirty = false;
        ShowValue();
        _readout.Text = $"Could not read “{text.Trim()}”, so it was not changed.";
        _readout.Visibility = Visibility.Visible;
    }

    // ---- Chips ---------------------------------------------------------------------------------------------------

    private void Rebuild()
    {
        _chips.Children.Clear();
        if (ShowChips)
        {
            if (Lean == DateLean.Past)
            {
                Chip("Now", () => Set(Today(), NowTime()));
                Chip("Yesterday", () => Set(Today().AddDays(-1), Time ?? NowTime()));
                Chip("2 days ago", () => Set(Today().AddDays(-2), Time ?? NowTime()));
            }
            else if (ShowTime)
            {
                Chip("Today", () => Set(Today(), Time));
                Chip("Tomorrow", () => Set(Today().AddDays(1), Time));
                Chip("In a week", () => Set(Today().AddDays(7), Time));
            }
            else
            {
                // A deadline is weeks or months away.
                Chip("In a week", () => Set(Today().AddDays(7), null));
                Chip("In a month", () => Set(Today().AddMonths(1), null));
                Chip("In 3 months", () => Set(Today().AddMonths(3), null));
            }
            if (AllowEmpty) Chip("None", () => Set(null, null));
        }
        _chips.Visibility = _chips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _times.Visibility = _exact.Visibility = ShowTime ? Visibility.Visible : Visibility.Collapsed;
        _panelButtons.Children.Clear();
        PanelButton(Lean == DateLean.Past && ShowTime ? "Now" : "Today", () => Set(Today(), Lean == DateLean.Past && ShowTime ? NowTime() : Time), false);
        if (AllowEmpty) PanelButton("None", () => { Set(null, null); _popup.IsOpen = false; }, false);
        PanelButton("Done", () => { ApplyExact(); _popup.IsOpen = false; }, true);
        ShowValue();
    }

    private void Chip(string text, Action pick)
    {
        var chip = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(12), FontSize = 12 };
        chip.Click += (_, _) => pick();
        _chips.Children.Add(chip);
    }

    private void PanelButton(string text, Action click, bool primary)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary };
        b.Click += (_, _) => click();
        _panelButtons.Children.Add(b);
    }

    // ---- The panel -----------------------------------------------------------------------------------------------

    private void OpenPanel()
    {
        Commit();
        SyncPanel();
        _popup.IsOpen = true;
        if (_times.SelectedItem is { } item) _times.ScrollIntoView(item);
    }

    private void SyncPanel()
    {
        _syncing = true;
        var day = Date ?? Today();
        _calendar.SelectedDate = Date;
        _calendar.DisplayDate = day;
        var time = Time;
        _times.SelectedItem = time is { } t && t.Minutes % 15 == 0 ? DateTimeText.Clock(new TimeSpan(t.Hours, t.Minutes, 0)) : null;
        _hour.Text = time is { } h ? h.Hours.ToString("00", CultureInfo.InvariantCulture) : "";
        _minute.Text = time is { } m ? m.Minutes.ToString("00", CultureInfo.InvariantCulture) : "";
        _syncing = false;
    }

    private void ApplyExact()
    {
        if (_syncing || !ShowTime) return;
        if (!int.TryParse(_hour.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h > 23) return;
        var m = int.TryParse(_minute.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var mm) && mm <= 59 ? mm : 0;
        var time = new TimeSpan(h, m, 0);
        if (time != Time) Set(Date ?? Today(), time);
    }
}
