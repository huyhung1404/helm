using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The player under the text (see the XAML). Seeking happens when the slider is let go, not while it moves.</summary>
public partial class NovelPlayerBar : UserControl
{
    public NovelPlayerBar()
    {
        InitializeComponent();
        // After the slider took the release (a tap on the bar moves it there first).
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(Seek), RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerCaptureLostEvent, (_, _) => Dispatcher.UIThread.Post(Seek), RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.KeyUp += (_, _) => Seek();
    }

    /// <summary>The speed: the reading options (voice, speed, sleep timer).</summary>
    public event EventHandler? OptionsRequested;

    private int _seekedTo = -1;

    private void Seek()
    {
        if (DataContext is not NovelReaderViewModel vm) return;
        var paragraph = (int)Math.Round(SeekBar.Value);
        if (paragraph == _seekedTo && paragraph == vm.PlayerParagraph) return;
        _seekedTo = paragraph;
        vm.SeekTo(paragraph);
    }

    /// <summary>A phone: the sentence, the bar, the place, then the buttons in the middle; a wide screen: one row.</summary>
    public void SetNarrow(bool narrow)
    {
        void Place(Control c, int row, int column, int span)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            Grid.SetColumnSpan(c, span);
        }
        if (narrow)
        {
            Place(StatusRow, 0, 0, 3);
            Place(SeekArea, 1, 0, 3);
            Place(PlaceArea, 2, 0, 3);
            Place(Buttons, 3, 0, 3);
            Place(LoadingBar, 4, 0, 3);
            SeekArea.Margin = new Thickness(0, 2, 0, 0);
            StatusRow.Margin = default;
            PlaceArea.HorizontalAlignment = HorizontalAlignment.Right;
            Buttons.HorizontalAlignment = HorizontalAlignment.Center;
            Buttons.Margin = new Thickness(0, 4, 0, 0);
        }
        else
        {
            Place(Buttons, 0, 0, 1);
            Place(SeekArea, 0, 1, 1);
            Place(PlaceArea, 0, 2, 1);
            Place(StatusRow, 1, 0, 3);
            Place(LoadingBar, 2, 0, 3);
            SeekArea.Margin = new Thickness(16, 0);
            StatusRow.Margin = new Thickness(0, 6, 0, 0);
            PlaceArea.HorizontalAlignment = HorizontalAlignment.Stretch;
            Buttons.HorizontalAlignment = HorizontalAlignment.Left;
            Buttons.Margin = default;
        }
    }

    private void Speed_Click(object? sender, RoutedEventArgs e) => OptionsRequested?.Invoke(this, EventArgs.Empty);

    private void CurrentSentence_Tapped(object? sender, TappedEventArgs e) => (DataContext as NovelReaderViewModel)?.FollowReadingCommand.Execute(null);
}
