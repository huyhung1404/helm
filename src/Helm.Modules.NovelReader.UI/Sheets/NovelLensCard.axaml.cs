using System.Globalization;
using Avalonia.Controls;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>Novel Lens in the settings sheet (see the XAML), read again from <see cref="INovelLens"/> after each change.</summary>
public partial class NovelLensCard : UserControl
{
    private readonly INovelLens? _lens;
    private bool _showing;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelLensCard() => InitializeComponent();

    public NovelLensCard(INovelLens lens, NovelReaderViewModel viewModel)
    {
        _lens = lens;
        InitializeComponent();
        OnButton.Click += (_, _) =>
        {
            lens.TurnOn();
            Show();
        };
        OffButton.Click += (_, _) =>
        {
            lens.TurnOff();
            Show();
        };
        CreateButton.Click += (_, _) =>
        {
            if (NewTitle.Text?.Trim() is not { Length: > 0 } title) return;
            lens.CreateBook(title);
            NewTitle.Text = "";
            Show();
        };
        BookBox.SelectionChanged += (_, _) =>
        {
            if (!_showing && BookBox.SelectedIndex >= 0) lens.BookIndex = BookBox.SelectedIndex;
        };
        TapSlider.ValueChanged += (_, _) =>
        {
            TapLabel.Text = TapSlider.Value.ToString("P0", CultureInfo.InvariantCulture) + " of the width";
            if (!_showing) lens.NextTap = Math.Round(TapSlider.Value, 2);
        };
        // Back from Accessibility settings: the state may have changed.
        AttachedToVisualTree += (_, _) => Show();
    }

    private void Show()
    {
        if (_lens is null) return;
        _lens.Refresh();
        _showing = true;
        try
        {
            State.Text = _lens.State;
            Buttons.IsVisible = _lens.IsSupported;
            OnButton.Content = _lens.IsShown ? "Accessibility settings" : "Turn on Novel Lens";
            OffButton.IsVisible = _lens.IsShown;
            Grid.SetColumnSpan(OnButton, _lens.IsShown ? 1 : 2);
            BookBox.ItemsSource = _lens.Books;
            BookBox.SelectedIndex = Math.Clamp(_lens.BookIndex, 0, Math.Max(0, _lens.Books.Count - 1));
            TapSlider.Value = _lens.NextTap;
            TapLabel.Text = _lens.NextTap.ToString("P0", CultureInfo.InvariantCulture) + " of the width";
        }
        finally
        {
            _showing = false;
        }
    }
}
