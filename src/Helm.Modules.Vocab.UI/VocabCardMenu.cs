using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab.Ui;

/// <summary>
/// The Learn card's "More" (⋯), as a sheet: I know it (back in a month), hide it from the cards, open its page, edit
/// it. Editing keeps the Learn tab in front: the editor opens over the card.
/// </summary>
public static class VocabCardMenu
{
    public static SheetHandle? Show(Control anchor, VocabViewModel vm)
    {
        var card = vm.Card;
        if (card.WordId is not { } id) return null;
        SheetHandle? sheet = null;
        void Then(Action action)
        {
            sheet?.Close();
            action();
        }

        var body = new StackPanel
        {
            Spacing = 10,
            Styles = { VocabSheetStyles.Create() },
            Children =
            {
                new StackPanel
                {
                    Margin = new Avalonia.Thickness(0, 0, 0, 4),
                    Children =
                    {
                        new TextBlock { Classes = { "eyebrow" }, Text = "THIS CARD" },
                        new TextBlock { Text = card.Text, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    },
                },
                Row(Symbol.Star, "I know it", "It comes back in a month", () => Then(() => card.KnowCommand.Execute(null))),
                Row(Symbol.EyeOff, "Hide from the cards", "Not on the Learn tab or the pop-up; Words → Hidden shows it again", () => Then(() => card.HideCommand.Execute(null))),
                Row(Symbol.Open, "Open its page", "Its whole tree, examples and family on the Words tab", () => Then(() => vm.Open(id))),
                Row(Symbol.Edit, "Edit", "Its meaning, examples, labels…", () => Then(() =>
                {
                    var tab = vm.Tab;
                    vm.Open(id);
                    vm.Tab = tab;
                    vm.Detail?.Editor.BeginCommand.Execute(null);
                })),
            },
        };
        sheet = Sheet.Show(anchor, body, 400);
        return sheet;
    }

    /// <summary>A row of a sheet's menu: its icon, its name and what it does.</summary>
    internal static Button Row(Symbol symbol, string name, string what, Action click, bool danger = false)
    {
        var icon = new SymbolIcon { Symbol = symbol, FontSize = 18, Margin = new Avalonia.Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (danger)
        {
            icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("HelmDanger"));
            title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("HelmDanger"));
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, new TextBlock { Text = what, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap } } };
        Grid.SetColumn(text, 1);
        var button = new Button { Classes = { "menurow" }, Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { icon, text } } };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
