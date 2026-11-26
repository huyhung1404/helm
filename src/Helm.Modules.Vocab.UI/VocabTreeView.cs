using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Helm.Modules.Vocab.Ui;

/// <summary>
/// How a word is built, drawn top-down on a wide screen: the word's box, a line down, its parts side by side under a
/// bar, each part's own parts under it. Tap a box to see the part (the selected one is outlined); right-click a part
/// that is a word by itself (a character, an inner word) to add it as a word. A narrow screen shows the outline instead
/// (<see cref="VocabOutlineRow"/>). Built in code: every box knows which halves of the bar it joins.
/// </summary>
public sealed class VocabTreeView : ContentControl
{
    public static readonly StyledProperty<VocabNodeViewModel?> RootProperty =
        AvaloniaProperty.Register<VocabTreeView, VocabNodeViewModel?>(nameof(Root));

    private const double Line = 1.5;
    private const double Gap = 14;

    public VocabNodeViewModel? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RootProperty) Content = Root is { } root ? Build(root) : null;
    }

    private static Control Build(VocabNodeViewModel node)
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (!node.IsRoot && (node.LineFromLeft || node.LineToRight)) column.Children.Add(Up(node));
        column.Children.Add(Box(node));
        if (node.HasChildren)
        {
            column.Children.Add(new Border { Classes = { "treeline" }, Width = Line, Height = Gap, HorizontalAlignment = HorizontalAlignment.Center });
            var children = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var child in node.Children) children.Children.Add(Build(child));
            column.Children.Add(children);
        }
        return column;
    }

    /// <summary>The bar over a part: from the left neighbour, to the right one, and the stem down into the box.</summary>
    private static Control Up(VocabNodeViewModel node)
    {
        var grid = new Grid { Height = Gap, ColumnDefinitions = new ColumnDefinitions("*,*") };
        if (node.LineFromLeft) grid.Children.Add(new Border { Classes = { "treeline" }, Height = Line, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, -Line / 2, 0) });
        if (node.LineToRight)
        {
            var right = new Border { Classes = { "treeline" }, Height = Line, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-Line / 2, 0, 0, 0) };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        var stem = new Border { Classes = { "treeline" }, Width = Line, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumnSpan(stem, 2);
        grid.Children.Add(stem);
        return grid;
    }

    private static Control Box(VocabNodeViewModel node)
    {
        var lines = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (node.HasRole)
        {
            var role = new TextBlock { Text = node.RoleText.ToUpperInvariant(), Classes = { "role" }, HorizontalAlignment = HorizontalAlignment.Center };
            role.Classes.Set("sound", node.IsSound);
            lines.Children.Add(role);
        }
        lines.Children.Add(new TextBlock { Text = node.Form, FontSize = node.IsRoot ? 22 : 19, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        if (node.HasShortMeaning)
            lines.Children.Add(new TextBlock { Text = node.ShortMeaning, Classes = { "nodemeaning" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });

        var box = new Button { Classes = { "node" }, Content = lines, Margin = new Thickness(5, 0), HorizontalAlignment = HorizontalAlignment.Center, Command = node.SelectCommand };
        box.Classes.Set("root", node.IsRoot);
        box.Classes.Set("picked", node.IsSelected && !node.IsRoot);
        AutomationProperties.SetName(box, node.Form);
        if (node.Node.Meaning.Length > 0) ToolTip.SetTip(box, node.Node.Meaning);
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VocabNodeViewModel.IsSelected)) box.Classes.Set("picked", node.IsSelected && !node.IsRoot);
        };
        // Right-click: make this part (挽 of 挽昼) one of your words, or open it when it is one already.
        if (node.CanAddAsWord)
        {
            var item = new MenuItem { Header = node.AddAsWordText, Command = node.AddAsWordCommand };
            box.ContextMenu = new ContextMenu { Items = { item } };
        }
        return box;
    }
}
