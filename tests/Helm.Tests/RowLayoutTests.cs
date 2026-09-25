using System.Windows;
using System.Windows.Controls;
using Helm.Core.Ui;

namespace Helm.Tests;

public class RowLayoutTests
{
    [Fact]
    public void Text_never_runs_under_the_control_on_a_narrow_row() => RunSta(() =>
    {
        var text = new TextBlock { Text = string.Join(" ", Enumerable.Repeat("Downloads in the background", 6)), TextWrapping = TextWrapping.Wrap };
        var control = new Border { Width = 120, Height = 32 };
        var row = new RowLayout { Spacing = 16 };
        row.Children.Add(text);
        row.Children.Add(control);

        row.Measure(new Size(400, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 400, row.DesiredSize.Height));

        var textRight = text.TranslatePoint(new Point(text.RenderSize.Width, 0), row).X;
        var controlLeft = control.TranslatePoint(new Point(0, 0), row).X;
        Assert.Equal(400 - 120, controlLeft, 1);
        Assert.True(textRight <= controlLeft - 16 + 0.5, $"text ends at {textRight}, control starts at {controlLeft}");
        Assert.True(text.RenderSize.Height > 20, "long text should wrap onto several lines");
    });

    [Fact]
    public void Several_controls_line_up_on_the_right_in_order() => RunSta(() =>
    {
        var text = new TextBlock { Text = "Status" };
        var first = new Border { Width = 80, Height = 20 };
        var second = new Border { Width = 50, Height = 20 };
        var row = new RowLayout { Spacing = 10 };
        row.Children.Add(text);
        row.Children.Add(first);
        row.Children.Add(second);

        row.Measure(new Size(500, 100));
        row.Arrange(new Rect(0, 0, 500, 40));

        Assert.Equal(450, second.TranslatePoint(new Point(0, 0), row).X, 1);
        Assert.Equal(360, first.TranslatePoint(new Point(0, 0), row).X, 1);
    });

    [Fact]
    public void Collapsed_controls_take_no_space() => RunSta(() =>
    {
        var text = new TextBlock { Text = "Status" };
        var hidden = new Border { Width = 80, Height = 20, Visibility = Visibility.Collapsed };
        var shown = new Border { Width = 50, Height = 20 };
        var row = new RowLayout { Spacing = 10 };
        row.Children.Add(text);
        row.Children.Add(hidden);
        row.Children.Add(shown);

        row.Measure(new Size(300, 100));
        row.Arrange(new Rect(0, 0, 300, 40));

        Assert.Equal(250, shown.TranslatePoint(new Point(0, 0), row).X, 1);
    });

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
