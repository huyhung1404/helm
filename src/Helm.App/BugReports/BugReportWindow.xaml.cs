using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Shell.BugReports;

namespace Helm.App.BugReports;

/// <summary>
/// The report window (Ctrl+Alt+F12, the title bar's bug button, the tray menu): over every app like Quick Capture's
/// panel, but it stays when another window takes the focus (picking a picture, copying something), so nothing typed is
/// lost. Esc closes it at once when empty, else asks for a second Esc. Closing thaws the black box.
/// </summary>
internal partial class BugReportWindow : Window
{
    private readonly BugReportFormViewModel _form;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private bool _escapeOnce;

    public BugReportWindow(BugReportFormViewModel form, IWindowService windows, IMonitorService monitors, bool canHandOff)
    {
        _form = form;
        _windows = windows;
        _monitors = monitors;
        DataContext = form;
        InitializeComponent();
        HandOffButton.Visibility = canHandOff ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) => _form.Close();
        Loaded += (_, _) =>
        {
            Place();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Place);
            _windows.Activate(new WindowInteropHelper(this).Handle);
            Input.Focus();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddImages(BugImage.FromFiles(files));
        };
    }

    /// <summary>Open the bug on the Bug reports page (the id).</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>Hand the bug to Claude Code from the page (the id).</summary>
    public event EventHandler<string>? HandOffRequested;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            var empty = string.IsNullOrWhiteSpace(_form.Text) && _form.Images.Count == 0;
            if (empty || _form.IsDone || _escapeOnce) { Close(); return; }
            _escapeOnce = true;
            EscHint.Text = "Press Esc again to discard";
            EscHint.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            return;
        }
        _escapeOnce = false;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_form.SendCommand.CanExecute(null)) _form.SendCommand.Execute(null);
            return;
        }
        // Ctrl+V with a picture (or picture files) on the clipboard adds them; text pastes as usual.
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !_form.IsDone)
        {
            var images = BugImage.FromClipboard();
            if (images.Count == 0) return;
            e.Handled = true;
            AddImages(images);
        }
    }

    private void AddImages(IReadOnlyList<BugImageDraft> images)
    {
        foreach (var image in images)
            if (!_form.AddImage(image)) break;
    }

    private void AddImage_Click(object sender, RoutedEventArgs e) => AddImages(BugImage.Pick(this));

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_form.SentId is { } id) OpenRequested?.Invoke(this, id);
        Close();
    }

    private void HandOff_Click(object sender, RoutedEventArgs e)
    {
        if (_form.SentId is { } id) HandOffRequested?.Invoke(this, id);
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>A fifth down the monitor under the mouse, centred, like Quick Capture.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || _monitors.FromPoint(_windows.GetCursorPosition()) is not { } monitor) return;
        var scale = monitor.Dpi / 96.0;
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scale);
        var work = monitor.WorkArea;
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        var y = work.Top + Math.Max(0, Math.Min(work.Height / 5, work.Height - height));
        _windows.SetWindowPosition(hwnd, new PixelPoint(x, y));
    }
}
