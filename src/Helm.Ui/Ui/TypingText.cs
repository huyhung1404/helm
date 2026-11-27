using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Helm.Core.Ui;

/// <summary>
/// What a text box shows right now, including the word an input method is still composing. Helm Keyboard composes every
/// word until a space or Enter, and Avalonia keeps that word out of <see cref="TextBox.Text"/> until then, so a search
/// bound to Text only searched after Enter. A search box binds this instead of Text:
/// <c>core:TypingText.Text="{Binding Query}"</c> (two-way: every letter goes to the view model; a value the view model
/// sets, such as clearing the search, goes into the box). <see cref="HelmTextPresenter"/> keeps it up to date.
/// </summary>
public static class TypingText
{
    public static readonly AttachedProperty<string> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBox, string>("Text", typeof(TypingText), "", defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The text shown, as the presenter last saw it.</summary>
    private static readonly AttachedProperty<string> ShownProperty =
        AvaloniaProperty.RegisterAttached<TextBox, string>("Shown", typeof(TypingText), "");

    static TypingText()
    {
        TextProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            // The view model set a value the box does not show (cleared it, restored a filter): put it in the box.
            var value = e.GetNewValue<string>() ?? "";
            if (value != box.GetValue(ShownProperty)) box.Text = value;
        });
    }

    public static string GetText(TextBox box) => box.GetValue(TextProperty);

    public static void SetText(TextBox box, string value) => box.SetValue(TextProperty, value);

    /// <summary>The presenter's text or composing word changed: the box shows <paramref name="shown"/>.</summary>
    internal static void Update(TextBox box, string shown)
    {
        box.SetValue(ShownProperty, shown);
        if (box.GetValue(TextProperty) != shown) box.SetCurrentValue(TextProperty, shown);
    }
}
