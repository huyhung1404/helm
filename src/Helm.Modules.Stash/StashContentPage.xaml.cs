using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Helm.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Stash;

/// <summary>
/// Stash itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). Files and text
/// dropped on the page or pasted with Ctrl+V go into the stash. While the tool is off the list stays visible but
/// read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class StashContentPage : Page
{
    private readonly StashModule _module;
    private readonly StashViewModel _viewModel;
    private readonly ILogger<StashContentPage> _logger;

    public StashContentPage(StashModule module, StashViewModel viewModel, ILogger<StashContentPage> logger)
    {
        _module = module;
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
        InitializeComponent();
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StashViewModel.IsTextBoxOpen) && viewModel.IsTextBoxOpen)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TextBox.Focus());
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        // Ctrl+V outside a text box adds what is on the clipboard (a text box pastes into itself first).
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (_, _) => PasteFromClipboard(), (_, e) => e.CanExecute = CanAdd));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Paste, Key.V, ModifierKeys.Control));
        DragOver += OnDragOver;
        Drop += OnDrop;
        ApplyEnabled();
    }

    private bool CanAdd => _module.IsEnabled && _viewModel.IsNotBusy;

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteFromClipboard();

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
            if (WindowsStashPlatform.FromClipboardImage(DateTimeOffset.Now) is { } picture)
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
            _logger.LogWarning(ex, "Pasting into the stash failed");
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
            _logger.LogWarning(ex, "Dropping into the stash failed");
            _viewModel.Message = $"Could not add what was dropped: {ex.Message}";
        }
    }

    /// <summary>Files go in; folders are named in a message (their files can be added themselves).</summary>
    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (files.Count > 0) await _viewModel.AddSourcesAsync(files.Select(WindowsStashPlatform.FromPath).ToList());
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
