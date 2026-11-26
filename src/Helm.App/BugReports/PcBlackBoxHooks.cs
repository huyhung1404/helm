using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Helm.Core.Diagnostics;

namespace Helm.App.BugReports;

/// <summary>
/// What the user does in Helm's windows, into the black box: clicks (and clicks on disabled buttons), shortcuts, picks in
/// drop-downs, windows shown, and how much was typed in which field — never what. Class handlers, so every Avalonia
/// window of Helm (the tools' too) is covered with no change to them.
/// </summary>
internal static class PcBlackBoxHooks
{
    private const int MaxLabel = 60;
    private static readonly ConditionalWeakTable<TextBox, StrongBox<int>> Typing = new();
    private static BlackBox? _box;
    private static Func<string?>? _where;
    private static WeakReference<TextBox>? _typingIn;

    /// <param name="where">The page in front (the active tab's title), for "where" in each entry.</param>
    public static void Install(BlackBox box, Func<string?> where)
    {
        if (_box is not null) return;
        _box = box;
        _where = where;
        Button.ClickEvent.AddClassHandler<Button>((b, e) => OnClick(b, e), handledEventsToo: true);
        MenuItem.ClickEvent.AddClassHandler<MenuItem>((m, e) => OnClick(m, e), handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((t, e) => OnPointerPressed(t, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((t, e) => OnKeyDown(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        TextBox.TextChangedEvent.AddClassHandler<TextBox>((t, _) => OnTextChanged(t), handledEventsToo: true);
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((t, e) => { if (ReferenceEquals(e.Source, t)) FlushTyping(t); }, handledEventsToo: true);
        SelectingItemsControl.SelectionChangedEvent.AddClassHandler<ComboBox>((c, e) => OnPicked(c, e), handledEventsToo: true);
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
            _box?.Navigation($"Opened window \"{Clip(w.Title is { Length: > 0 } t ? t : w.GetType().Name)}\"", w.GetType().Name));
    }

    /// <summary>Records the typing still pending in the focused field (before the box freezes).</summary>
    public static void Flush()
    {
        if (_typingIn is { } weak && weak.TryGetTarget(out var box)) FlushTyping(box);
    }

    private static void OnClick(Control control, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, control)) return;
        var what = control switch
        {
            CheckBox c => $"{(c.IsChecked == true ? "Ticked" : "Unticked")} {Label(control)}",
            ToggleButton t => $"Switched {Label(control)} {(t.IsChecked == true ? "on" : "off")}",
            _ => $"Clicked {Label(control)}",
        };
        _box?.Input(what, Where());
    }

    private static void OnPointerPressed(TopLevel top, PointerPressedEventArgs e)
    {
        try
        {
            foreach (var visual in top.GetVisualsAt(e.GetPosition(top)))
            {
                for (var v = visual; v is not null; v = v.GetVisualParent())
                {
                    if (v is not Button button) continue;
                    if (!button.IsEffectivelyEnabled)
                        _box?.Record(BlackBoxKind.DisabledClick, $"Clicked {Label(button)} while it was disabled", Where());
                    return;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Hit testing while the tree changes.
        }
    }

    private static void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.ImeProcessed or Key.DeadCharProcessed) return;
        var mods = e.KeyModifiers;
        var command = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta);
        var special = e.Key is Key.Enter or Key.Escape or Key.Delete || e.Key is >= Key.F1 and <= Key.F24;
        // Plain typing is never recorded, only shortcuts and the keys that do something by themselves.
        if (!command && !special) return;
        var parts = new List<string>(4);
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(e.Key.ToString());
        _box?.Input($"Pressed {string.Join("+", parts)}", Where());
    }

    private static void OnTextChanged(TextBox box)
    {
        if (!box.IsFocused) return;
        _typingIn = new WeakReference<TextBox>(box);
        Typing.GetOrCreateValue(box).Value++;
    }

    private static void FlushTyping(TextBox box)
    {
        if (!Typing.TryGetValue(box, out var count) || count.Value == 0) return;
        var changes = count.Value;
        count.Value = 0;
        _box?.Input($"Typed in {Label(box)} ({changes} change{(changes == 1 ? "" : "s")}, text not recorded)", Where());
    }

    private static void OnPicked(ComboBox combo, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, combo) || e.AddedItems.Count == 0 || !combo.IsDropDownOpen && !combo.IsFocused) return;
        var item = e.AddedItems[0] switch
        {
            ComboBoxItem { Content: string s } => s,
            string s => s,
            var other => other?.ToString() ?? "",
        };
        _box?.Input($"Picked \"{Clip(item)}\" in {Label(combo)}", Where());
    }

    private static string? Where() => _where?.Invoke();

    /// <summary>What a person would call the control: its accessible name, its text, its tooltip, or its kind.</summary>
    internal static string Label(Control control)
    {
        if (AutomationProperties.GetName(control) is { Length: > 0 } name) return Quote(name);
        var text = control switch
        {
            MenuItem { Header: string h } => h,
            ContentControl { Content: string s } => s,
            ContentControl { Content: TextBlock tb } => tb.Text,
            ContentControl { Content: Visual v } => FirstText(v),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(text)) return Quote(text);
        if (ToolTip.GetTip(control) is string { Length: > 0 } tip) return Quote(tip);
        if (control is TextBox { PlaceholderText: { Length: > 0 } placeholder }) return Quote(placeholder);
        if (control.Name is { Length: > 0 } n) return n;
        return "a " + control.GetType().Name;
    }

    private static string? FirstText(Visual root)
    {
        if (root is TextBlock { Text: { Length: > 0 } t }) return t;
        foreach (var child in root.GetVisualChildren())
            if (FirstText(child) is { } found) return found;
        return null;
    }

    private static string Quote(string text) => $"\"{Clip(text.ReplaceLineEndings(" ").Trim())}\"";

    private static string Clip(string text) => text.Length <= MaxLabel ? text : text[..(MaxLabel - 1)] + "…";
}
