using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using Helm.Core.Ui;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Shell.Views;

/// <summary>
/// The phone's open tabs (the title bar's count): a sheet with one glass row per tab, the shown one outlined; tap a row
/// to show it, its X to close it.
/// </summary>
public static class TabSwitcher
{
    public static SheetHandle? Show(Control anchor, TabStripViewModel strip)
    {
        var list = new StackPanel { Spacing = 8 };
        SheetHandle? handle = null;
        list.Children.Add(new TextBlock { Classes = { "eyebrow" }, Text = "OPEN TABS" });
        foreach (var tab in strip.Tabs)
        {
            var item = tab;
            var close = new Button
            {
                Classes = { "chip" },
                Width = 36,
                Height = 36,
                Padding = default,
                Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 14 },
                [ToolTip.TipProperty] = "Close tab",
                [AutomationProperties.NameProperty] = item.CloseName,
            };
            close.Click += (_, _) =>
            {
                item.CloseCommand.Execute(null);
                handle?.Close();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(new ModuleIcon { Symbol = item.Glyph, Image = item.Icon, Size = 20 });
            var title = new TextBlock { Text = item.Title, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            Grid.SetColumn(close, 2);
            row.Children.Add(close);
            var show = new Button
            {
                Classes = { "glass" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(14, 8, 8, 8),
                Content = row,
                [AutomationProperties.NameProperty] = item.Title,
            };
            show.Classes.Set("selected", item.IsActive);
            show.Click += (_, _) =>
            {
                item.ShowCommand.Execute(null);
                handle?.Close();
            };
            list.Children.Add(show);
        }
        handle = Sheet.Show(anchor, list);
        return handle;
    }
}
