using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Notes.Ui;

/// <summary>What each platform does its own way around the shared Notes page.</summary>
public interface INotesPageHost
{
    /// <summary>Save every note as a Markdown file: a folder picker on desktop. False: the platform has no export.</summary>
    bool CanExport => false;

    void Export() { }

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>Small converters of the Notes views.</summary>
public static class NotesConverters
{
    /// <summary>A card's menu: "Unpin" for a pinned note, else "Pin to the top".</summary>
    public static readonly IValueConverter PinText = new FuncValueConverter<bool, string>(pinned => pinned ? "Unpin" : "Pin to the top");
}

/// <summary>
/// Notes itself, the same view on Windows, Android, macOS and iOS: the wall of cards under a bar that stays on top
/// (search, New note, the trash, the gear), and the open note over the wall (wide) or in its place (a phone, below
/// 720 px). While the tool is off the notes stay visible but read only. Keys: Ctrl+F search, Ctrl+N new, Ctrl+S save,
/// Esc closes the note.
/// </summary>
public partial class NotesView : UserControl
{
    private const double Narrow = 720;
    private readonly IModule? _module;
    private readonly NotesViewModel? _viewModel;
    private readonly INotesPageHost? _host;
    private bool _narrow;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NotesView() => InitializeComponent();

    public NotesView(IModule module, NotesViewModel viewModel, INotesPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Confirmations (delete for good, empty the trash) are the same glass sheet on every platform.
        viewModel.UseDialogs(new GlassDialogs(() => this));
        InitializeComponent();
        Editor.DataContext = viewModel;
        TopBar.SizeChanged += (_, _) => FitUnderBar();
        KeyDown += OnKeyDown;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", _narrow);
        var side = _narrow ? 16 : 40;
        TopBar.Padding = new Thickness(side, _narrow ? 14 : 20, side, 12);
        MenuButton.IsVisible = _host?.OpenMenu is not null;
        FitUnderBar();
        // Phone: two narrow columns of cards; wide: as many as fit, each at least 220.
        if (Wall() is { } wall) wall.MinColumnWidth = _narrow ? 150 : 220;
        Editor.SetNarrow(_narrow);
        // The open note floats over the wall on a wide screen; on a phone it is the page.
        Scrim.IsVisible = !_narrow;
        EditorCard.Margin = _narrow ? default : new Thickness(32, 24);
        EditorCard.CornerRadius = _narrow ? default : new CornerRadius(26);
        EditorCard.BorderThickness = _narrow ? default : new Thickness(1);
        ApplyEditorOpen();
    }

    /// <summary>The cards start under the bar that stays on top (and slide under it when scrolled).</summary>
    private void FitUnderBar()
    {
        var side = _narrow ? 16 : 40;
        Page.Margin = new Thickness(side, TopBar.Bounds.Height + 10, side, 40);
        Scroller.Padding = new Thickness(0, 0, _narrow ? 0 : 14, 14);
    }

    private MasonryPanel? Wall() => this.FindDescendantOfType<MasonryPanel>();

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);

    /// <summary>The gear: Notes' settings as a sheet over the page.</summary>
    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _module is null) return;
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new NotesSettingsView(_viewModel, _module, _host, () => sheet?.Close()), 480);
    }

    /// <summary>A click beside the open note closes it (it is saved as you type).</summary>
    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e) => _viewModel?.CloseEditor();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Escape && _viewModel.IsEditorOpen)
        {
            _viewModel.CloseEditor();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            if (_viewModel.IsEditorOpen) _viewModel.CloseEditor();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N && _module?.IsEnabled != false)
        {
            _viewModel.NewNoteCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            _viewModel.Flush();
            e.Handled = true;
        }
    }

    // The module and the view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.EditorFocusRequested += OnFocusRequested;
        }
        if (_module is not null) _module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
        ApplyEditorOpen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.EditorFocusRequested -= OnFocusRequested;
            // Leaving the page (another tool, Home) saves what was typed.
            _viewModel.Flush();
        }
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotesViewModel.IsEditorOpen)) ApplyEditorOpen();
        else if (e.PropertyName == nameof(NotesViewModel.ShowingTrash)) Scroller.Offset = default;
    }

    /// <summary>The note grows into place; on a phone the wall steps aside while it is open.</summary>
    private void ApplyEditorOpen()
    {
        var open = _viewModel?.IsEditorOpen == true;
        WallLayer.IsVisible = !(open && _narrow);
        if (open) Motion.Enter(EditorCard);
    }

    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            // A new note starts in its title; an opened one in its text.
            if (_viewModel is { EditTitle.Length: 0, EditBody.Length: 0 }) Editor.FocusTitle();
            else Editor.FocusBody();
        }, DispatcherPriority.Background);

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        NewButton.IsEnabled = _module.IsEnabled;
        Editor.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
        Eyebrow.Text = DateTime.Now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
    }
}
