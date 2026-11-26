using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Helm.Core.Ui;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>
/// The report form (<see cref="BugReportFormViewModel"/>), the same on every platform: the PC hosts it in its small
/// report window, the phone on its report page. Pictures come from the platform (<see cref="IBugReportFormHost"/>),
/// a paste (Ctrl+V) or a drop of picture files. With a keyboard, Ctrl+Enter sends and Escape closes (an empty form at
/// once, else at the second Escape). Leaving the form (<see cref="IBugReportFormHost.Close"/>) thaws the black box.
/// </summary>
public partial class BugReportFormView : UserControl
{
    /// <summary>The form is a card-sized view: it stacks its fields below this width (the PC window is wider).</summary>
    private const double NarrowBelow = 560;
    private readonly BugReportFormViewModel? _form;
    private readonly IBugReportFormHost? _host;
    private bool _escapeOnce;
    private bool _boxOpen;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public BugReportFormView() => InitializeComponent();

    public BugReportFormView(BugReportFormViewModel form, IBugReportFormHost host)
    {
        _form = form;
        _host = host;
        DataContext = form;
        InitializeComponent();
        KeyHint.IsVisible = host.HasKeyboard;
        CameraButton.IsVisible = host.CanTakePhoto;
        PickText.Text = host.CanTakePhoto ? "Gallery" : "Add a picture";
        PictureHint.Text = host.HasKeyboard
            ? "Or paste (Ctrl+V) or drop pictures here: screenshots you take yourself. Helm never takes screenshots."
            : "Screenshots you take yourself, a photo, or a picture you copied. Helm never takes screenshots.";
        ArrangePictureButtons();
        BoxTags.IsVisible = form.HasErrors || form.FreezeText is not null;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(Button.ClickEvent, OnButtonClick);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        form.PropertyChanged += OnFormChanged;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Input);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < NarrowBelow;
        Classes.Set("narrow", narrow);
        Body.Margin = narrow ? new Thickness(16, 18, 16, 24) : new Thickness(28, 22, 28, 24);
        // How bad and the tool side by side; on a phone the tool goes under it, full width.
        Fields.ColumnDefinitions = ColumnDefinitions.Parse(narrow ? "*" : "3*,2*");
        Fields.RowSpacing = narrow ? 14 : 0;
        Grid.SetColumn(ToolField, narrow ? 0 : 1);
        Grid.SetRow(ToolField, narrow ? 1 : 0);
    }

    /// <summary>The picture buttons share their row equally (two on the PC, three with a camera).</summary>
    private void ArrangePictureButtons()
    {
        var buttons = new[] { PickButton, CameraButton, PasteButton }.Where(b => b.IsVisible).ToList();
        PictureButtons.ColumnDefinitions = ColumnDefinitions.Parse(string.Join(",", buttons.Select(_ => "*")));
        for (var i = 0; i < buttons.Count; i++) Grid.SetColumn(buttons[i], i);
    }

    private void OnFormChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BugReportFormViewModel.IsDone) && _form?.IsDone == true)
            Dispatcher.UIThread.Post(() => Scroller.Offset = default);
    }

    // ---- keys ----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_form is null || _host is null) return;
        if (e.Key == Key.Escape)
        {
            // A sheet over the form (a picture) closes first.
            if (e.Handled) return;
            e.Handled = true;
            var empty = string.IsNullOrWhiteSpace(_form.Text) && _form.Images.Count == 0;
            if (empty || _form.IsDone || _escapeOnce)
            {
                Leave();
                return;
            }
            _escapeOnce = true;
            KeyHint.IsVisible = true;
            KeyHint.Text = "Press Esc again to discard";
            KeyHint.Classes.Set("late", true);
            return;
        }
        if (_escapeOnce)
        {
            _escapeOnce = false;
            KeyHint.IsVisible = _host.HasKeyboard;
            KeyHint.Text = "Ctrl+Enter to send · Esc to close";
            KeyHint.Classes.Set("late", false);
        }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            if (_form.SendCommand.CanExecute(null)) _form.SendCommand.Execute(null);
            return;
        }
        // Ctrl+V: text pastes as usual; a copied picture (or copied picture files) is added beside it.
        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control && !_form.IsDone)
            _ = PasteAsync(quiet: true);
    }

    // ---- pictures ----

    private void Add(IEnumerable<BugImageDraft> images)
    {
        foreach (var image in images)
            if (_form?.AddImage(image) != true) break;
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if (_form is null || _host is null) return;
        try
        {
            Add(await _host.PickAsync(BugStore.MaxImages - _form.Images.Count));
        }
        catch (Exception ex)
        {
            _form.Error = $"No picture added: {ex.Message}";
        }
    }

    private async void Camera_Click(object? sender, RoutedEventArgs e)
    {
        if (_form is null || _host is null) return;
        try
        {
            if (await _host.TakePhotoAsync() is { } photo) _form.AddImage(photo);
        }
        catch (Exception ex)
        {
            _form.Error = $"No photo taken: {ex.Message}";
        }
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e) => await PasteAsync(quiet: false);

    /// <param name="quiet">From Ctrl+V: no picture on the clipboard is not a problem (it was text).</param>
    private async Task PasteAsync(bool quiet)
    {
        if (_form is null || _host is null) return;
        try
        {
            var images = await _host.PasteAsync();
            if (images.Count > 0) Add(images);
            else if (!quiet) _form.Error = "There is no picture on the clipboard.";
        }
        catch (Exception ex)
        {
            if (!quiet) _form.Error = $"Nothing pasted: {ex.Message}";
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _form is { IsDone: false } && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_form is not { IsDone: false } form || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;
        e.Handled = true;
        var images = new List<BugImageDraft>();
        foreach (var file in files.OfType<IStorageFile>())
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                if (stream.CanSeek && stream.Length > BugStore.MaxImageBytes * 2L) continue;
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                if (BugPictureFiles.Draft(memory.ToArray(), file.Name) is { } draft) images.Add(draft);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip what cannot be read.
            }
        }
        if (images.Count == 0) form.Error = "Only pictures can be added (PNG, JPEG, GIF, BMP, WebP).";
        Add(images);
    }

    private SheetHandle? _picture;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _picture?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { DataContext: BugImageItem image } button || _form is null || _host is null) return;
        if (button.Classes.Contains("unpick"))
        {
            e.Handled = true;
            _form.RemoveImageCommand.Execute(image);
        }
        else if (button.Classes.Contains("thumb"))
        {
            e.Handled = true;
            _picture?.Close();
            SheetHandle? sheet = null;
            sheet = Sheet.Show(this, new BugPictureView(image, _host, () => sheet?.Close()), 640);
            _picture = sheet;
        }
    }

    // ---- the black box, leaving ----

    private void ToggleBox_Click(object? sender, RoutedEventArgs e) => ShowEvents(!_boxOpen);

    /// <summary>Opens or folds the black box's events under its header.</summary>
    public void ShowEvents(bool open)
    {
        _boxOpen = open;
        BoxEvents.IsVisible = open;
        Motion.SetTurn(BoxChevron, open);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button) _host?.DragWindow();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Leave();

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        var id = _form?.SentId;
        Leave();
        if (id is not null) _host?.OpenBug(id);
    }

    /// <summary>Done with the form, sent or not: the black box records again and the host closes it.</summary>
    private void Leave()
    {
        _picture?.Close();
        _form?.Close();
        _host?.Close();
    }
}
