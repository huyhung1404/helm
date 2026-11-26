using Avalonia.Controls;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The held sentence's sheet (see the XAML); closing it lets the sentence go.</summary>
public partial class NovelSentenceSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelSentenceSheet() => InitializeComponent();

    public NovelSentenceSheet(HeldSentenceViewModel held, Action close)
    {
        DataContext = held;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
    }
}
