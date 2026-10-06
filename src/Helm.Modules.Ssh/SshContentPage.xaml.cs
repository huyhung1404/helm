using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the servers stay visible but nothing connects, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class SshContentPage : Page
{
    private readonly SshModule _module;
    private readonly SshViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public SshContentPage(SshModule module, SshViewModel viewModel, IShellNavigation navigation, IDialogService dialogs, IClipboardService clipboard,
        ISettingsStoreFactory settings, ILogger<SshContentPage> logger)
    {
        _module = module;
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // WebView2 keeps its profile in Helm's cache (the install folder may not be writable). Nothing of the session
        // is stored there: the page's files come from resources and the terminal keeps no history on disk.
        Terminal.Attach(viewModel, dialogs, clipboard, logger, Path.Combine(settings.Paths.Root, "cache", SshIds.ModuleId, "webview2"));
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        viewModel.Menu.PropertyChanged += OnMenuChanged;
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Picker.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConnectAsync();
    }

    /// <summary>View glue: a PasswordBox does not bind. The password goes to this one connection and the box is emptied.</summary>
    private async Task ConnectAsync()
    {
        var password = PasswordBox.Password;
        PasswordBox.Password = "";
        try
        {
            await _viewModel.ConnectAsync(password);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // async void callers: never let an unexpected failure take Helm down.
            _viewModel.Message = "Connecting failed: " + ex.Message;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(SshPage));

    private void OnMenuChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SshMenuViewModel.Columns):
                Dispatcher.BeginInvoke(BuildTableColumns);
                break;
            case nameof(SshMenuViewModel.Output):
                // A log follows its end, unless the reader scrolled up.
                Dispatcher.BeginInvoke(() =>
                {
                    if (MenuOutput.VerticalOffset + MenuOutput.ViewportHeight >= MenuOutput.ExtentHeight - 24) MenuOutput.ScrollToEnd();
                });
                break;
            case nameof(SshMenuViewModel.Succeeded):
                Dispatcher.BeginInvoke(() => MenuStatus.Foreground = _viewModel.Menu.Succeeded switch
                {
                    true => (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush"),
                    false => (System.Windows.Media.Brush)FindResource("SystemFillColorCriticalBrush"),
                    _ => (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush"),
                });
                break;
        }
    }

    /// <summary>View glue: the table's columns come from the server, so the grid is built when a table arrives.</summary>
    private void BuildTableColumns()
    {
        MenuTable.Columns.Clear();
        foreach (var column in _viewModel.Menu.Columns)
            MenuTable.Columns.Add(new DataGridTextColumn { Header = column.Title, Binding = new System.Windows.Data.Binding($"[{column.Id}]") });
        if (_viewModel.Menu.Rows.Any(r => r.Actions.Count > 0))
            MenuTable.Columns.Add(new DataGridTemplateColumn { Header = "", CellTemplate = (DataTemplate)FindResource("RowActions") });
    }

    private async void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MenuRowAction action } button) return;
        var row = FindRow(button);
        if (row is null) return;
        try
        {
            await _viewModel.Menu.RunRowActionAsync(row, action.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _viewModel.Menu.Status = "Could not run: " + ex.Message;
        }
    }

    private static MenuTableRow? FindRow(DependencyObject element)
    {
        for (var e = element; e is not null; e = System.Windows.Media.VisualTreeHelper.GetParent(e))
            if (e is FrameworkElement { DataContext: MenuTableRow row }) return row;
        return null;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => _viewModel.ImportOpenSsh(SshPage.OpenSshFolder);

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NewHostCommand.Execute(null);
        _navigation.ShowPage(typeof(SshPage));
    }
}
