using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Helm.Modules.QuickCapture;

/// <summary>View glue for the capture box: Enter saves, Tab switches the target, Ctrl+1…9 picks one.</summary>
public partial class CapturePanel : UserControl
{
    private static readonly TimeSpan DoneFor = TimeSpan.FromMilliseconds(900);

    private readonly CaptureBoxViewModel _viewModel;
    private readonly DispatcherTimer _doneTimer;

    public CapturePanel(CaptureBoxViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _doneTimer = new DispatcherTimer { Interval = DoneFor };
        _doneTimer.Tick += (_, _) =>
        {
            _doneTimer.Stop();
            Finished?.Invoke(this, EventArgs.Empty);
        };
        viewModel.Saved += (_, message) =>
        {
            DoneText.Text = message;
            // Keep the height: the box does not jump while the message shows.
            Done.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Hidden;
            _doneTimer.Start();
        };
        Input.PreviewKeyDown += OnInputKeyDown;
    }

    /// <summary>The confirmation after a save has been shown: the window can close.</summary>
    public event EventHandler? Finished;

    /// <summary>The window is shown again: back to the editor, the cursor at the end of the text.</summary>
    public void Prepare(string? text = null, string? targetId = null)
    {
        _doneTimer.Stop();
        Done.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        _viewModel.Reset(text, targetId);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Input.Focus();
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;
        });
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when mods == ModifierKeys.None:
                e.Handled = true;
                _viewModel.Save();
                break;
            case Key.Tab when mods is ModifierKeys.None or ModifierKeys.Shift:
                e.Handled = true;
                _viewModel.Cycle(mods == ModifierKeys.Shift ? -1 : 1);
                break;
            case >= Key.D1 and <= Key.D9 when mods == ModifierKeys.Control:
                e.Handled = true;
                _viewModel.SelectIndex(e.Key - Key.D1);
                break;
        }
    }
}
