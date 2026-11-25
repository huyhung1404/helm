using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Helm.Core.Ui;

/// <summary>
/// What a desktop window lends a shared page: the title, the page inset and scrolling. On Android the shell's app bar
/// and scroller do this, so the shared view itself carries none of it.
/// </summary>
public sealed class PageFrame : UserControl
{
    public PageFrame(string title, Control page)
    {
        var stack = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        stack.Children.Add(new TextBlock { Text = title, Classes = { "pagetitle" } });
        stack.Children.Add(page);
        Content = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        // A hosted window cannot show WPF's Mica through it, so the page paints a solid surface.
        this.Bind(BackgroundProperty, this.GetResourceObservable("HelmPageBackground"));
    }
}
