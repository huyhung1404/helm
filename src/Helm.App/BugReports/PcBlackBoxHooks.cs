using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Core.Diagnostics;

namespace Helm.App.BugReports;

/// <summary>
/// What the user does in Helm's windows, into the black box: clicks (and clicks on disabled buttons), shortcuts, picks in
/// drop-downs, pages and windows shown, and how much was typed in which field — never what. Class handlers, so every
/// WPF window of Helm (tools' panels too) is covered with no change to them.
/// </summary>
internal static class PcBlackBoxHooks
{
    private const int MaxLabel = 60;
    private static readonly ConditionalWeakTable<TextBoxBase, StrongBox<int>> Typing = new();
    private static BlackBox? _box;

    public static void Install(BlackBox box)
    {
        if (_box is not null) return;
        _box = box;
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler(OnClick), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(TextBoxBase), TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ComboBox), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnPicked), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Page), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnPageLoaded), handledEventsToo: true);
    }

    /// <summary>Records the typing still pending in the focused field (before the box freezes).</summary>
    public static void Flush()
    {
        if (Keyboard.FocusedElement is TextBoxBase box) FlushTyping(box);
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        var what = sender switch
        {
            CheckBox c => $"{(c.IsChecked == true ? "Ticked" : "Unticked")} {Label(sender)}",
            ToggleButton t => $"Switched {Label(sender)} {(t.IsChecked == true ? "on" : "off")}",
            _ => $"Clicked {Label(sender)}",
        };
        _box?.Input(what, Where(sender));
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window) return;
        try
        {
            if (VisualTreeHelper.HitTest(window, e.GetPosition(window))?.VisualHit is not DependencyObject hit) return;
            for (var d = hit; d is not null; d = Parent(d))
            {
                if (d is not (ButtonBase or MenuItem)) continue;
                if (d is UIElement { IsEnabled: false } disabled)
                    _box?.Record(BlackBoxKind.DisabledClick, $"Clicked {Label(disabled)} while it was disabled", Where(disabled));
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // Hit testing a window being torn down.
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.ImeProcessed or Key.DeadCharProcessed) return;
        var mods = Keyboard.Modifiers;
        var command = mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt) || mods.HasFlag(ModifierKeys.Windows);
        var special = key is Key.Enter or Key.Escape or Key.Delete || key is >= Key.F1 and <= Key.F24;
        // Plain typing is never recorded, only shortcuts and the keys that do something by themselves.
        if (!command && !special) return;
        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key == Key.Return ? "Enter" : key.ToString());
        _box?.Input($"Pressed {string.Join("+", parts)}", Where(Keyboard.FocusedElement as DependencyObject ?? (DependencyObject)sender));
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBoxBase box || !box.IsKeyboardFocusWithin) return;
        Typing.GetOrCreateValue(box).Value++;
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBoxBase box && ReferenceEquals(e.OriginalSource, sender)) FlushTyping(box);
    }

    private static void FlushTyping(TextBoxBase box)
    {
        if (!Typing.TryGetValue(box, out var count) || count.Value == 0) return;
        var changes = count.Value;
        count.Value = 0;
        _box?.Input($"Typed in {Label(box)} ({changes} change{(changes == 1 ? "" : "s")}, text not recorded)", Where(box));
    }

    private static void OnPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not ComboBox { IsLoaded: true } combo || e.AddedItems.Count == 0) return;
        if (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen && !combo.IsMouseOver) return; // set by code, not picked
        var item = e.AddedItems[0] switch
        {
            ComboBoxItem { Content: string s } => s,
            string s => s,
            _ => combo.Text,
        };
        _box?.Input($"Picked \"{Clip(item)}\" in {Label(combo)}", Where(combo));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window w && ReferenceEquals(e.OriginalSource, sender))
            _box?.Navigation($"Opened window \"{Clip(w.Title is { Length: > 0 } t ? t : w.GetType().Name)}\"", w.GetType().Name);
    }

    private static void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Page p && ReferenceEquals(e.OriginalSource, sender))
            _box?.Navigation($"Showed {Clip(p.Title is { Length: > 0 } t ? t : p.GetType().Name)}", p.GetType().Name);
    }

    /// <summary>What a person would call the control: its accessible name, its text, its tooltip, or its kind.</summary>
    internal static string Label(object element)
    {
        if (element is DependencyObject d && AutomationProperties.GetName(d) is { Length: > 0 } name) return Quote(name);
        var text = element switch
        {
            HeaderedItemsControl { Header: string h } => h,
            ContentControl { Content: string s } => s,
            ContentControl { Content: TextBlock tb } => tb.Text,
            ContentControl { Content: DependencyObject content } => FirstText(content),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(text)) return Quote(text);
        if (element is FrameworkElement { ToolTip: string tip } && tip.Length > 0) return Quote(tip);
        if (element is Wpf.Ui.Controls.TextBox { PlaceholderText: { Length: > 0 } placeholder }) return Quote(placeholder);
        if (element is FrameworkElement { Name: { Length: > 0 } n }) return n;
        return "a " + element.GetType().Name;
    }

    /// <summary>The page (or window) the control is on.</summary>
    internal static string? Where(object element)
    {
        for (var d = element as DependencyObject; d is not null; d = Parent(d))
        {
            if (d is Page { Title: { Length: > 0 } page }) return page;
            if (d is Window { Title: { Length: > 0 } window }) return window;
            if (d is Window w) return w.GetType().Name;
        }
        return null;
    }

    private static string? FirstText(DependencyObject root)
    {
        if (root is TextBlock { Text: { Length: > 0 } t }) return t;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            if (FirstText(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static string Quote(string text) => $"\"{Clip(text.ReplaceLineEndings(" ").Trim())}\"";

    private static string Clip(string text) => text.Length <= MaxLabel ? text : text[..(MaxLabel - 1)] + "…";
}
