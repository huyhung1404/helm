using Avalonia.Controls;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The reading options (see the XAML). "All settings" swaps it for the settings sheet.</summary>
public partial class NovelOptionsSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelOptionsSheet() => InitializeComponent();

    public NovelOptionsSheet(NovelReaderViewModel viewModel, bool hasPointer, Action close, Action settings)
    {
        DataContext = viewModel;
        InitializeComponent();
        SizeHint.Text = hasPointer ? "Also Ctrl + the mouse wheel, Ctrl + = and Ctrl + −." : "Also two fingers on the text.";
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        SettingsButton.Click += (_, _) => settings();
    }
}
