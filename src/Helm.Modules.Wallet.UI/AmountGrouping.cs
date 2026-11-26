using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.Wallet.Ui;

/// <summary>
/// Thousands separators while an amount is typed, so 1000000 reads 1.000.000 (or 1,000,000); the caret stays put. Every
/// <c>TextBox.amount</c> under the control it is attached to.
/// </summary>
internal static class AmountGrouping
{
    private static readonly ConditionalWeakTable<TextBox, string> Texts = [];
    private static bool _grouping;

    public static void Attach(Control root) => root.AddHandler(TextBox.TextChangedEvent, OnTextChanged, RoutingStrategies.Bubble);

    private static void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_grouping || e.Source is not TextBox { Text: { } current } box || !box.Classes.Contains("amount")) return;
        var previous = Texts.TryGetValue(box, out var before) ? before : "";
        var (text, caret) = WalletFormat.GroupDigits(current, box.CaretIndex, previous);
        Texts.AddOrUpdate(box, text);
        if (text == current) return;
        _grouping = true;
        try
        {
            box.Text = text;
            box.CaretIndex = caret;
        }
        finally
        {
            _grouping = false;
        }
    }
}
