using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Scratch.Ui;

/// <summary>What each platform does its own way around the shared Scratch page.</summary>
public interface IScratchPageHost
{
    /// <summary>Said in the empty page: how to put something in ("Press Ctrl+V…", "share to Helm Scratch").</summary>
    string AddHint { get; }

    /// <summary>The system clipboard: files or a picture, else its text, into Scratch (Ctrl+V and the Paste button).</summary>
    Task PasteAsync();

    /// <summary>Files dropped on the page (local paths); the platform makes them into things to add. False: it takes none.</summary>
    Task<bool> AddPathsAsync(IReadOnlyList<string> paths) => Task.FromResult(false);

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>
/// Scratch itself, the same view on Windows, Android, macOS and iOS (see the notes in the XAML). Keys while the page shows
/// (wide): Ctrl+V adds, Ctrl+F searches, Ctrl+A selects all, Ctrl+C copies, Ctrl+Shift+C sends to every clipboard,
/// Ctrl+Shift+N to Notes, Del deletes (Shift+Del for good), Enter opens, Esc clears the selection.
/// </summary>
public partial class ScratchView : UserControl
{
    private const double Narrow = 720;

    /// <summary>How long a finger rests on a card before it lifts (a shorter touch is a tap or a scroll).</summary>
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(450);

    /// <summary>Moving further than this before the card lifts is a scroll (touch) or the start of a drag (mouse).</summary>
    private const double Slop = 10;

    private readonly IModule? _module;
    private readonly ScratchViewModel? _viewModel;
    private readonly IScratchPageHost? _host;
    private readonly DispatcherTimer _hold;
    private bool _narrow;
    private ScratchRowViewModel? _pressed;
    private IPointer? _pointer;
    private Point _pressedAt;
    private Control? _lifted;
    private KeyModifiers _pressedModifiers;
    private bool _doubleClick;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public ScratchView()
    {
        _hold = new DispatcherTimer { Interval = HoldTime };
        InitializeComponent();
    }

    public ScratchView(IModule module, ScratchViewModel viewModel, IScratchPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Confirmations (delete for good, empty the trash) are the same glass sheet on every platform.
        viewModel.UseDialogs(new GlassDialogs(() => this));
        _hold = new DispatcherTimer { Interval = HoldTime };
        _hold.Tick += (_, _) => Lift();
        InitializeComponent();
        AddHint.Text = host.AddHint;
        TopBar.SizeChanged += (_, _) => FitUnderBar();
        KeyDown += OnKeyDown;
        // Tunnel and handled-too: the card's content is a button, which handles the press itself.
        Wall.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Wall.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Wall.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Wall.AddHandler(PointerCaptureLostEvent, (_, _) => Drop(null), RoutingStrategies.Bubble, handledEventsToo: true);
        Wall.AddHandler(Button.ClickEvent, OnCardClick);
        Wall.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        // Files and text dropped on the page go into Scratch.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", _narrow);
        var side = _narrow ? 16 : 40;
        TopBar.Margin = new Thickness(side, _narrow ? 14 : 20, side, 12);
        TrashFab.Margin = new Thickness(0, 0, side, _narrow ? 20 : 28);
        MenuButton.IsVisible = _host?.OpenMenu is not null;
        // The bar: one row on a wide page; on a phone the search on its own row, the buttons under it.
        Grid.SetColumn(SearchCard, _narrow ? 0 : 1);
        Grid.SetColumnSpan(SearchCard, _narrow ? 4 : 1);
        TopBar.RowSpacing = _narrow ? 10 : 0;
        Grid.SetColumn(AddActions, _narrow ? 0 : 2);
        Grid.SetColumnSpan(AddActions, _narrow ? 3 : 1);
        Grid.SetRow(AddActions, _narrow ? 1 : 0);
        Grid.SetRow(SettingsButton, _narrow ? 1 : 0);
        FitUnderBar();
        if (Wall.ItemsPanelRoot is MasonryPanel wall) wall.MinColumnWidth = _narrow ? 150 : 220;
        // The title and the filters: side by side on a wide page, the filters under the title on a phone.
        Grid.SetColumn(FilterScroller, _narrow ? 0 : 1);
        Grid.SetRow(FilterScroller, _narrow ? 1 : 0);
        FilterScroller.HorizontalAlignment = _narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        FilterScroller.Margin = _narrow ? new Thickness(0, 12, 0, 0) : default;
        Grid.SetColumnSpan(FilterScroller, _narrow ? 2 : 1);
        ApplyDetail();
    }

    /// <summary>The cards start under the bar that stays on top (their top edge fades instead of cutting a card).</summary>
    private void FitUnderBar()
    {
        var side = _narrow ? 16 : 40;
        Scroller.Margin = new Thickness(0, TopBar.Margin.Top + TopBar.Bounds.Height, 0, 0);
        Page.Margin = new Thickness(side, 14, side, 96);
        Scroller.Padding = new Thickness(0, 0, _narrow ? 0 : 14, 14);
    }

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    /// <summary>The gear: Scratch's settings as a sheet over the page.</summary>
    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _module is null) return;
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new ScratchSettingsView(_viewModel, _module, () => sheet?.Close()), 480);
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        if (_host is null || _module?.IsEnabled == false) return;
        await _host.PasteAsync();
    }

    // ---- Selecting, opening -------------------------------------------------------------------------------------

    /// <summary>
    /// A click selects the card (the keyboard then acts on it; on a phone the details open); Shift or Ctrl + click adds it to
    /// the selection or takes it out (wide); a double click opens it.
    /// </summary>
    private void OnCardClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || e.Source is not Button { DataContext: ScratchRowViewModel row } button || !button.Classes.Contains("cardhit")) return;
        if (_doubleClick)
        {
            _doubleClick = false;
            _viewModel.OpenCommand.Execute(row);
            return;
        }
        if (!_narrow && _pressedModifiers.HasFlag(KeyModifiers.Shift) || _pressedModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.ToggleSelect(row);
            return;
        }
        _viewModel.SelectCommand.Execute(row);
    }

    /// <summary>The right-click menu is for the PC; on a phone a hold lifts the card instead.</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_narrow || _lifted is not null) e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || _module is null || !_module.IsEnabled) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        // A text box keeps its own keys.
        if (e.Source is TextBox) return;
        if (ctrl && !shift && e.Key == Key.V)
        {
            if (_viewModel.IsNotBusy && _host is not null) _ = _host.PasteAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _viewModel.Selected is not null)
        {
            _viewModel.CloseDetail();
            e.Handled = true;
            return;
        }
        if (ctrl && !shift && e.Key == Key.A)
        {
            _viewModel.SelectAll();
            e.Handled = true;
            return;
        }
        if (_viewModel.SelectionCount == 0) return;
        var single = _viewModel.SelectionCount == 1 ? _viewModel.Selected : null;
        switch (e.Key)
        {
            case Key.C when ctrl && shift:
                if (single is { IsLive: true }) _viewModel.SendToClipboardCommand.Execute(single);
                break;
            case Key.C when ctrl:
                _ = _viewModel.CopySelectedAsync();
                break;
            case Key.N when ctrl && shift:
                _ = _viewModel.SendSelectedToNotesAsync();
                break;
            case Key.Delete when shift:
                // Like Shift+Delete in Explorer: for good, without the trash (it asks first).
                _ = _viewModel.DeleteSelectedForeverAsync();
                break;
            case Key.Delete:
                // In the trash, deleting is for good (it asks first).
                _ = _viewModel.DeleteSelectedAsync();
                break;
            case Key.Enter:
                if (single is not null) _viewModel.OpenCommand.Execute(single);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---- Reordering: drag a card onto another one (a phone: hold it first) ------------------------------------------

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Drop(null);
        _pressedModifiers = e.KeyModifiers;
        _doubleClick = e.ClickCount == 2 && e.Pointer.Type == PointerType.Mouse;
        if (_viewModel is null || _module is null || !_viewModel.CanReorder || !_module.IsEnabled || InsideSmallButton(e.Source as Visual)) return;
        _pressed = RowAt(e.Source as Visual);
        if (_pressed is null) return;
        _pointer = e.Pointer;
        _pressedAt = e.GetPosition(Wall);
        // A finger lifts after a hold; a mouse starts dragging as soon as it moves.
        if (e.Pointer.Type != PointerType.Mouse) _hold.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var at = e.GetPosition(Wall);
        if (_lifted is null)
        {
            if (_pressed is null || Distance(at, _pressedAt) <= Slop) return;
            // A mouse button held and moved: lift now. A finger moving before the hold: a scroll, not a drag.
            if (e.Pointer.Type == PointerType.Mouse && e.GetCurrentPoint(Wall).Properties.IsLeftButtonPressed) Lift();
            else Cancel();
            return;
        }
        _lifted.RenderTransform = new TranslateTransform(at.X - _pressedAt.X, at.Y - _pressedAt.Y);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_lifted is null)
        {
            Cancel();
            return;
        }
        // The release ends the drag: the card's button must not take it as a click.
        e.Handled = true;
        Drop(e.GetPosition(Wall));
    }

    private void Lift()
    {
        _hold.Stop();
        if (_pressed is null || Wall.ContainerFromItem(_pressed) is not Control card) return;
        _lifted = card;
        card.Opacity = 0.75;
        card.ZIndex = 1;
        _pointer?.Capture(Wall);
    }

    /// <summary>Puts the lifted card down: on the card under <paramref name="at"/> (moves it there), or back in its place.</summary>
    private void Drop(Point? at)
    {
        _hold.Stop();
        var card = _lifted;
        var row = _pressed;
        _lifted = null;
        _pressed = null;
        if (card is null) return;
        card.RenderTransform = null;
        card.Opacity = 1;
        card.ZIndex = 0;
        if (_pointer?.Captured == Wall) _pointer.Capture(null);
        _pointer = null;
        if (at is { } point && row is not null && TargetAt(point, card) is { } target) _viewModel?.Move(row.Id, target.Id);
    }

    private void Cancel()
    {
        _hold.Stop();
        _pressed = null;
        _pointer = null;
    }

    /// <summary>The card under a point of the wall, other than the one being dragged.</summary>
    private ScratchRowViewModel? TargetAt(Point point, Control dragged)
    {
        if (Wall.ItemsPanelRoot is not { } panel) return null;
        var inPanel = Wall.TranslatePoint(point, panel) ?? point;
        foreach (var child in panel.Children)
        {
            if (ReferenceEquals(child, dragged) || !child.Bounds.Contains(inPanel)) continue;
            return child.DataContext as ScratchRowViewModel;
        }
        return null;
    }

    private static ScratchRowViewModel? RowAt(Visual? element)
    {
        for (var current = element; current is not null; current = current.GetVisualParent())
            if (current is StyledElement { DataContext: ScratchRowViewModel row }) return row;
        return null;
    }

    /// <summary>The card's own buttons (Copy, Trash) stay buttons.</summary>
    private static bool InsideSmallButton(Visual? element)
    {
        for (var current = element; current is not null; current = current.GetVisualParent())
        {
            if (current is Button button && button.Classes.Contains("small")) return true;
            if (current is ItemsControl) return false;
        }
        return false;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- Files and text dropped on the page ---------------------------------------------------------------------------

    private bool CanAdd => _module?.IsEnabled == true && _viewModel?.IsNotBusy == true;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAdd && (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!CanAdd || _viewModel is null || _host is null) return;
        e.Handled = true;
        try
        {
            if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
            {
                var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
                if (paths.Count > 0 && await _host.AddPathsAsync(paths)) return;
            }
            if (e.DataTransfer.TryGetText() is { Length: > 0 } text) await _viewModel.AddTextAsync(text);
        }
        catch (Exception ex)
        {
            _viewModel.Message = $"Could not add what was dropped: {ex.Message}";
        }
    }

    // ---- The page ----------------------------------------------------------------------------------------------------

    // The module and the view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
        if (_module is not null) _module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
        ApplyDetail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        Drop(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScratchViewModel.IsTextBoxOpen) when _viewModel?.IsTextBoxOpen == true:
                Dispatcher.UIThread.Post(() => NewTextBox.Focus(), DispatcherPriority.Background);
                break;
            case nameof(ScratchViewModel.Selected):
                ApplyDetail();
                break;
            case nameof(ScratchViewModel.Filter):
                Scroller.Offset = default;
                break;
        }
    }

    /// <summary>On a phone one thing's details take the wall's place; on a wide page the selection only lights the card.</summary>
    private void ApplyDetail()
    {
        var open = _narrow && _viewModel?.Selected is not null && _viewModel.SelectionCount == 1;
        DetailLayer.IsVisible = open;
        WallLayer.IsVisible = !open;
        if (open) Motion.Enter(DetailLayer);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        FilesButton.IsEnabled = _module.IsEnabled && _viewModel?.IsNotBusy != false;
        TextButton.IsEnabled = _module.IsEnabled;
        PasteButton.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
