using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Helm.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). Files and text
/// dropped on the page or pasted with Ctrl+V go into Scratch. While the tool is off the list stays visible but
/// read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class ScratchContentPage : Page
{
    private readonly ScratchModule _module;
    private readonly ScratchViewModel _viewModel;
    private readonly ILogger<ScratchContentPage> _logger;

    public ScratchContentPage(ScratchModule module, ScratchViewModel viewModel, ILogger<ScratchContentPage> logger)
    {
        _module = module;
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
        InitializeComponent();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        // Ctrl+V outside a text box adds what is on the clipboard (a text box pastes into itself first).
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (_, _) => PasteFromClipboard(), (_, e) => e.CanExecute = CanAdd));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Paste, Key.V, ModifierKeys.Control));
        DragOver += OnDragOver;
        Drop += OnDrop;
        Wall.PreviewMouseLeftButtonDown += OnCardPressed;
        Wall.PreviewMouseMove += OnCardMoved;
        Wall.PreviewMouseLeftButtonUp += (_, _) => _pressed = null;
        Wall.DragOver += OnCardDragOver;
        // The ghost follows the cursor over the whole page, also where nothing can be dropped.
        PreviewDragOver += (_, e) => _ghost?.MoveTo(e.GetPosition(Root));
        Wall.Drop += OnCardDrop;
        // Keyboard on the selected card: Ctrl+C, Ctrl+Shift+C, Ctrl+D / Delete, Enter, Esc.
        KeyDown += OnKeyDown;
        ApplyEnabled();
    }

    private bool CanAdd => _module.IsEnabled && _viewModel.IsNotBusy;

    // ---- Reordering: drag a card onto another one ----------------------------------------------------------------

    private const string CardFormat = "Helm.Scratch.Card";
    private ScratchRowViewModel? _pressed;
    private Point _pressedAt;
    private DragGhost? _ghost;

    /// <summary>A click selects the card (the keyboard then acts on it), a double click opens it, a drag moves it.</summary>
    private void OnCardPressed(object sender, MouseButtonEventArgs e)
    {
        var row = InsideActionButton(e.OriginalSource as DependencyObject) ? null : RowAt(e.OriginalSource as DependencyObject);
        _pressed = row;
        _pressedAt = e.GetPosition(Wall);
        Wall.Focus();
        _viewModel.SelectCommand.Execute(row);
        if (row is not null && e.ClickCount == 2) _viewModel.OpenCommand.Execute(row);
    }

    // ---- Keyboard ------------------------------------------------------------------------------------------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // A text box (search) keeps its own keys.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || !_module.IsEnabled) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Escape && _viewModel.Selected is not null)
        {
            _viewModel.CloseDetail();
            e.Handled = true;
            return;
        }
        if (_viewModel.Selected is not { } row) return;
        switch (e.Key)
        {
            case Key.C when ctrl && shift:
                if (row.IsLive) _viewModel.SendToClipboardCommand.Execute(row);
                break;
            case Key.C when ctrl:
                _viewModel.CopyCommand.Execute(row);
                break;
            case Key.D when ctrl:
            case Key.Delete:
                // In the trash, deleting is for good (it asks first).
                if (row.Trashed) _viewModel.DeleteForeverCommand.Execute(row);
                else _viewModel.TrashCommand.Execute(row);
                break;
            case Key.Enter:
                _viewModel.OpenCommand.Execute(row);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnCardMoved(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } row || e.LeftButton != MouseButtonState.Pressed || !_viewModel.CanReorder || !_module.IsEnabled) return;
        var moved = e.GetPosition(Wall) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressed = null;
        var card = Wall.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
        var layer = AdornerLayer.GetAdornerLayer(Root);
        if (card is not null && layer is not null)
        {
            // The picture of the card follows the cursor; its place stays, faded, until it is dropped.
            _ghost = new DragGhost(Root, card, Wall.TranslatePoint(_pressedAt, card));
            _ghost.MoveTo(e.GetPosition(Root));
            layer.Add(_ghost);
        }
        if (card is not null) card.Opacity = 0.3;
        try
        {
            DragDrop.DoDragDrop(Wall, new DataObject(CardFormat, row.Id), DragDropEffects.Move);
        }
        finally
        {
            if (card is not null) card.Opacity = 1;
            if (_ghost is not null) layer?.Remove(_ghost);
            _ghost = null;
        }
    }

    private void OnCardDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(CardFormat)) return;
        e.Effects = _viewModel.CanReorder && RowAt(e.OriginalSource as DependencyObject) is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCardDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(CardFormat) is not string id) return;
        e.Handled = true;
        if (RowAt(e.OriginalSource as DependencyObject) is { } target) _viewModel.Move(id, target.Id);
    }

    /// <summary>The card under an element of the wall.</summary>
    private static ScratchRowViewModel? RowAt(DependencyObject? element)
    {
        for (var current = element; current is not null; current = ParentOf(current))
            if (current is FrameworkElement { DataContext: ScratchRowViewModel row }) return row;
        return null;
    }

    private static bool InsideActionButton(DependencyObject? element)
    {
        for (var current = element; current is not null; current = ParentOf(current))
        {
            if (current is Wpf.Ui.Controls.Button) return true;
            if (current is ItemsControl) return false;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    /// <summary>Files copied in Explorer, then a picture (a screenshot), then text.</summary>
    private async void PasteFromClipboard()
    {
        if (!CanAdd) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                await AddPathsAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
                return;
            }
            if (WindowsScratchPlatform.FromClipboardImage(DateTimeOffset.Now) is { } picture)
            {
                await _viewModel.AddSourcesAsync([picture]);
                return;
            }
            if (Clipboard.ContainsText())
            {
                await _viewModel.AddTextAsync(Clipboard.GetText());
                return;
            }
            _viewModel.Message = "The clipboard has nothing to add. Copy files, a picture or text first.";
        }
        catch (Exception ex)
        {
            // The clipboard is shared with every app: it can be busy or hold something broken.
            _logger.LogWarning(ex, "Pasting into Scratch failed");
            _viewModel.Message = $"Could not paste: {ex.Message}";
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepted = CanAdd && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText));
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!CanAdd) return;
        e.Handled = true;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                await AddPathsAsync(paths);
            else if (e.Data.GetData(DataFormats.UnicodeText) is string text)
                await _viewModel.AddTextAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dropping into Scratch failed");
            _viewModel.Message = $"Could not add what was dropped: {ex.Message}";
        }
    }

    /// <summary>Files go in; folders are named in a message (their files can be added themselves).</summary>
    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (files.Count > 0) await _viewModel.AddSourcesAsync(files.Select(WindowsScratchPlatform.FromPath).ToList());
        if (folders.Count > 0)
        {
            var note = folders.Count == 1 ? $"“{Path.GetFileName(folders[0])}” is a folder" : $"{folders.Count} folders were skipped";
            _viewModel.Message = $"{(files.Count > 0 ? _viewModel.Message + " " : "")}{note}: add the files inside it, or a .zip of it.".Trim();
        }
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        Actions.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>Visible only when every bound value is true (e.g. a file that is not in the trash).</summary>
public sealed class AllTrueToVisibility : IMultiValueConverter
{
    public static AllTrueToVisibility Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
