using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>pc:ButtonIcon.Symbol="Add24"</c> on a button styled with VocabStyles' Btn styles: the icon before its text
/// (<c>pc:ButtonIcon.After="ChevronRight24"</c>: after it, as Next →), drawn by the style's template at 16 px; an
/// IconBtn draws its symbol alone at the button's font size.
/// </summary>
public static class ButtonIcon
{
    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.RegisterAttached("Symbol", typeof(SymbolRegular), typeof(ButtonIcon), new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty AfterProperty =
        DependencyProperty.RegisterAttached("After", typeof(SymbolRegular), typeof(ButtonIcon), new PropertyMetadata(SymbolRegular.Empty));

    /// <summary>The button's fill under the pointer (each Btn style sets its own).</summary>
    public static readonly DependencyProperty HoverProperty =
        DependencyProperty.RegisterAttached("Hover", typeof(Brush), typeof(ButtonIcon), new PropertyMetadata(null));

    public static Brush? GetHover(DependencyObject o) => (Brush?)o.GetValue(HoverProperty);

    public static void SetHover(DependencyObject o, Brush? value) => o.SetValue(HoverProperty, value);

    public static SymbolRegular GetSymbol(DependencyObject o) => (SymbolRegular)o.GetValue(SymbolProperty);

    public static void SetSymbol(DependencyObject o, SymbolRegular value) => o.SetValue(SymbolProperty, value);

    public static SymbolRegular GetAfter(DependencyObject o) => (SymbolRegular)o.GetValue(AfterProperty);

    public static void SetAfter(DependencyObject o, SymbolRegular value) => o.SetValue(AfterProperty, value);
}
