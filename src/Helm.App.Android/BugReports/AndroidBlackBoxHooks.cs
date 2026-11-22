using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Helm.App.Android.ViewModels;
using Helm.Core.Diagnostics;
using Helm.Core.Platform;

namespace Helm.App.Android.BugReports;

/// <summary>
/// What the user does in Helm on the phone, into the black box: taps (and taps on disabled buttons), switches, picks,
/// the pages shown, Helm going to the background and back, and how much was typed in which box — never what. Class
/// handlers, so every Avalonia page of Helm (tool windows too) is covered with no change to them.
/// </summary>
internal static class AndroidBlackBoxHooks
{
    private const int MaxLabel = 60;
    private static readonly ConditionalWeakTable<TextBox, StrongBox<int>> Typing = new();
    private static BlackBox? _box;
    private static ShellNavigator? _navigator;
    private static WeakReference<TextBox>? _typingIn;

    public static void Install(BlackBox box, ShellNavigator navigator)
    {
        if (_box is not null) return;
        _box = box;
        _navigator = navigator;
        Button.ClickEvent.AddClassHandler<Button>((b, e) => OnClick(b, e), handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((t, e) => OnPointerPressed(t, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        TextBox.TextChangedEvent.AddClassHandler<TextBox>((t, _) => OnTextChanged(t), handledEventsToo: true);
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((t, e) => { if (ReferenceEquals(e.Source, t)) Flush(t); }, handledEventsToo: true);
        SelectingItemsControl.SelectionChangedEvent.AddClassHandler<ComboBox>((c, e) => OnPicked(c, e), handledEventsToo: true);
        navigator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellNavigator.ActiveTab) && navigator.ActiveTab is { } tab) box.Navigation($"Showed {tab.Title}", "Shell");
        };
        ActivityHost.Paused += (_, _) => box.Navigation("Helm went to the background", "Android");
        ActivityHost.Resumed += (_, _) => box.Navigation("Helm came back to the front", "Android");
    }

    /// <summary>Records the typing still pending in the focused box (before the box freezes).</summary>
    public static void Flush()
    {
        if (_typingIn is { } weak && weak.TryGetTarget(out var box)) Flush(box);
    }

    private static void OnClick(Button button, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, button)) return;
        var what = button switch
        {
            CheckBox c => $"{(c.IsChecked == true ? "Ticked" : "Unticked")} {Label(button)}",
            ToggleButton t => $"Switched {Label(button)} {(t.IsChecked == true ? "on" : "off")}",
            _ => $"Tapped {Label(button)}",
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
                        _box?.Record(BlackBoxKind.DisabledClick, $"Tapped {Label(button)} while it was disabled", Where());
                    return;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Hit testing while the tree changes.
        }
    }

    private static void OnTextChanged(TextBox box)
    {
        if (!box.IsFocused) return;
        _typingIn = new WeakReference<TextBox>(box);
        Typing.GetOrCreateValue(box).Value++;
    }

    private static void Flush(TextBox box)
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

    private static string? Where() => _navigator?.ActiveTab?.Title;

    /// <summary>What a person would call the control: its accessible name, its text, its tooltip, or its kind.</summary>
    internal static string Label(Control control)
    {
        if (AutomationProperties.GetName(control) is { Length: > 0 } name) return Quote(name);
        var text = control switch
        {
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
