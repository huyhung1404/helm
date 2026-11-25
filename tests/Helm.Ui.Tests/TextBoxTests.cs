using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Helm.Core.Ui;

namespace Helm.Ui.Tests;

public sealed class TextBoxTests
{
    private static (Window Window, TextBox Box) Show(string text = "")
    {
        var box = new TextBox { Text = text, CaretIndex = text.Length, Classes = { "clearButton" } };
        var window = new Window { Content = box, Width = 400, Height = 200 };
        window.Show();
        return (window, box);
    }

    private static TextPresenter Presenter(TextBox box) => box.GetVisualDescendants().OfType<TextPresenter>().Single();

    [AvaloniaFact]
    public void Every_text_box_uses_Helm_presenter()
    {
        var (window, box) = Show();
        Assert.IsType<HelmTextPresenter>(Presenter(box));
        window.Close();
    }

    [AvaloniaFact]
    public void Fluent_look_is_kept()
    {
        var (window, box) = Show();
        // Setters outside the template still come from Fluent's TextBox theme (HelmTheme bases its theme on it).
        Assert.True(box.MinHeight > 0);
        Assert.NotNull(box.ContextFlyout);
        window.Close();
    }

    [AvaloniaFact]
    public void Composing_word_shows_without_underline()
    {
        var (window, box) = Show("Gõ ");
        var presenter = Presenter(box);
        presenter.PreeditText = "việt";

        var layout = presenter.TextLayout;
        var runs = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().ToList();
        Assert.Equal("Gõ việt", string.Concat(runs.Select(r => r.Text.ToString())));
        Assert.All(runs, r => Assert.True(r.Properties.TextDecorations is null || r.Properties.TextDecorations.Count == 0));
        window.Close();
    }

    [AvaloniaFact]
    public void Avalonia_alone_underlines_the_composing_word()
    {
        // The control for the test above: if Avalonia stops underlining (an upgrade), HelmTextPresenter and the TextBox
        // template in HelmTheme can go.
        var presenter = new TextPresenter { Text = "Gõ ", CaretIndex = 3 };
        var window = new Window { Content = presenter, Width = 400, Height = 200 };
        window.Show();
        presenter.PreeditText = "việt";

        var runs = presenter.TextLayout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>();
        Assert.Contains(runs, r => r.Properties.TextDecorations is { Count: > 0 });
        window.Close();
    }

    [AvaloniaFact]
    public void Password_box_keeps_its_mask_while_composing()
    {
        var (window, box) = Show("ab");
        box.PasswordChar = '•';
        var presenter = Presenter(box);
        presenter.PreeditText = "c";

        var text = string.Concat(presenter.TextLayout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().Select(r => r.Text.ToString()));
        Assert.Equal("•••", text);
        window.Close();
    }
}
