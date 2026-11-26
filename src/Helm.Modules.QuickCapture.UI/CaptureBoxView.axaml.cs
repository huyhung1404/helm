using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Helm.Modules.QuickCapture.Ui;

/// <summary>View glue for the capture box: Enter saves, Tab switches the target, Ctrl+1…9 picks one.</summary>
public partial class CaptureBoxView : UserControl
{
    private static readonly TimeSpan DoneFor = TimeSpan.FromMilliseconds(900);

    private readonly CaptureBoxViewModel? _viewModel;
    private readonly DispatcherTimer _doneTimer;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public CaptureBoxView()
    {
        _doneTimer = new DispatcherTimer { Interval = DoneFor };
        InitializeComponent();
    }

    public CaptureBoxView(CaptureBoxViewModel viewModel)
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
            Editor.Opacity = 0;
            Editor.IsHitTestVisible = false;
            Done.IsVisible = true;
            _doneTimer.Start();
        };
        // Tunnel: the text box would take Enter and Tab itself.
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The confirmation after a save has been shown: the window can close.</summary>
    public event EventHandler? Finished;

    /// <summary>The window is shown again: back to the editor, the cursor at the end of the text.</summary>
    public void Prepare()
    {
        if (_viewModel is null) return;
        _doneTimer.Stop();
        Done.IsVisible = false;
        Editor.Opacity = 1;
        Editor.IsHitTestVisible = true;
        _viewModel.Reset();
        FocusInput();
    }

    /// <summary>The cursor goes into the text, at its end, so typing starts at once.</summary>
    public void FocusInput() =>
        Dispatcher.UIThread.Post(() =>
        {
            Input.Focus();
            Input.CaretIndex = Input.Text?.Length ?? 0;
        }, DispatcherPriority.Input);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Enter when mods == KeyModifiers.None:
                e.Handled = true;
                _viewModel.Save();
                break;
            case Key.Tab when mods is KeyModifiers.None or KeyModifiers.Shift:
                e.Handled = true;
                _viewModel.Cycle(mods == KeyModifiers.Shift ? -1 : 1);
                break;
            case >= Key.D1 and <= Key.D9 when mods == KeyModifiers.Control:
                e.Handled = true;
                _viewModel.SelectIndex(e.Key - Key.D1);
                break;
        }
    }
}
