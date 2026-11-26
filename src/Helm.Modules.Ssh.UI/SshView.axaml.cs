using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Modules;
using Helm.Core.Ui;

namespace Helm.Modules.Ssh.Ui;

/// <summary>
/// SSH itself, the same view on Windows, Android, macOS and iOS (see the notes in the XAML). The terminal is the
/// platform's (<see cref="ISshPageHost.CreateTerminal"/>); it is a native view drawn over the page, so it steps aside
/// while a sheet (the menu, the settings, a question) is open. While the tool is off the servers stay visible but
/// nothing connects.
/// </summary>
public partial class SshView : UserControl
{
    private const double Narrow = 720;

    private readonly IModule? _module;
    private readonly SshViewModel? _viewModel;
    private readonly ISshPageHost? _host;
    private readonly ISshTerminal? _terminal;
    private SheetHandle? _menu;
    private SheetHandle? _settings;
    private int _covering;
    private bool _narrow;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public SshView() => InitializeComponent();

    public SshView(IModule module, SshViewModel viewModel, ISshPageHost host)
    {
        _module = module;
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Every question (trust a server, delete one, a new key) in the same glass sheet, with the terminal out of the way.
        viewModel.UseDialogs(new TerminalSafeDialogs(new GlassDialogs(() => this), Covering));
        InitializeComponent();
        _terminal = host.CreateTerminal(viewModel);
        TerminalFrame.Child = _terminal.View;
        // The PC imports its OpenSSH config; elsewhere "Add my first server" fills the row.
        ImportFirstButton.IsVisible = host.OpenSshFolder is not null;
        if (host.OpenSshFolder is null) Grid.SetColumnSpan(AddFirstButton, 2);
        if (host.OpenSshFolder is null) Grid.SetColumn(AddFirstButton, 0);
        FirstStepText.Text = host.OpenSshFolder is null
            ? "Its address, and how Helm signs in: this device's key, a password, or Vault."
            : "Its address and how Helm signs in, or import them from your SSH config: same servers, same keys.";
    }

    /// <summary>The menu sheet and the settings sheet, while open (for the Gallery and the tests).</summary>
    public SheetHandle? MenuSheet => _menu;

    public SheetHandle? SettingsSheet => _settings;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        _narrow = narrow;
        Classes.Set("narrow", narrow);
        Page.Margin = narrow ? new Thickness(14, 16, 14, 12) : new Thickness(40, 28, 40, 24);
        MenuRow.IsVisible = _host?.OpenMenu is not null;
        FirstRunSteps.Columns = narrow ? 1 : 3;
        // The first run's two buttons share a row on a wide screen and stack on a phone.
        if (ImportFirstButton.IsVisible)
        {
            FirstRunButtons.ColumnDefinitions = ColumnDefinitions.Parse(narrow ? "*" : "*,*");
            FirstRunButtons.RowDefinitions = RowDefinitions.Parse(narrow ? "Auto,Auto" : "Auto");
            FirstRunButtons.RowSpacing = 10;
            Grid.SetColumn(AddFirstButton, narrow ? 0 : 1);
            Grid.SetRow(AddFirstButton, narrow ? 1 : 0);
        }
        // A phone keeps a quick "+ Server" at the end of the row; a wide screen adds them in the settings.
        NewServerButton.IsVisible = narrow;
        // The connect bar: beside the server's name on a wide screen, under it on a phone.
        Grid.SetColumn(ConnectActions, narrow ? 0 : 1);
        Grid.SetRow(ConnectActions, narrow ? 1 : 0);
        ConnectActions.Margin = narrow ? new Thickness(0, 10, 0, 0) : default;
        ConnectActions.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        PasswordBox.Width = narrow ? 170 : 220;
        AgentNote.IsVisible = !narrow && _viewModel?.ShowingAgent == true;
        UpdateTerminal();
    }

    // The module and view model outlive the page: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_module is null || _viewModel is null) return;
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Hosts.CollectionChanged += OnHostsChanged;
        _viewModel.TerminalFocusRequested += OnFocusRequested;
        _viewModel.Menu.Typed += OnMenuTyped;
        ApplyEnabled();
        UpdateTopLine();
        UpdateTerminal();
        Dispatcher.UIThread.Post(MarkSelected, DispatcherPriority.Loaded);
        try
        {
            _host?.Opened();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SSH page check failed: {ex}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Hosts.CollectionChanged -= OnHostsChanged;
            _viewModel.TerminalFocusRequested -= OnFocusRequested;
            _viewModel.Menu.Typed -= OnMenuTyped;
        }
        _menu?.Close();
        _settings?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The phone's Back: a sheet closes first. False: the platform goes back itself.</summary>
    public bool HandleBack() => Sheet.CloseTopmost();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SshViewModel.SelectedHost):
                MarkSelected();
                break;
            case nameof(SshViewModel.IsConnected) or nameof(SshViewModel.ActiveSession):
                UpdateTopLine();
                UpdateTerminal();
                break;
            case nameof(SshViewModel.ShowTerminal) or nameof(SshViewModel.HasSession):
                UpdateTerminal();
                break;
            case nameof(SshViewModel.ShowingAgent):
                AgentNote.IsVisible = !_narrow && _viewModel?.ShowingAgent == true;
                break;
        }
    }

    private void OnHostsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateTopLine();
        Dispatcher.UIThread.Post(MarkSelected, DispatcherPriority.Loaded);
    }

    /// <summary>The eyebrow: how many servers and how many are connected.</summary>
    private void UpdateTopLine()
    {
        if (_viewModel is null) return;
        var parts = new List<string> { "SSH" };
        var count = _viewModel.Hosts.Count;
        if (count > 0) parts.Add(count == 1 ? "1 server" : count.ToString(CultureInfo.InvariantCulture) + " servers");
        var connected = _viewModel.Hosts.Count(h => h.IsConnected);
        if (connected > 0) parts.Add(connected.ToString(CultureInfo.InvariantCulture) + " connected");
        TopLine.Text = string.Join(" · ", parts).ToUpper(CultureInfo.CurrentCulture);
    }

    /// <summary>The server shown has the strong outline.</summary>
    private void MarkSelected()
    {
        if (_viewModel is null) return;
        foreach (var button in HostList.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("server")))
        {
            var selected = ReferenceEquals(button.DataContext, _viewModel.SelectedHost);
            button.Classes.Set("selected", selected);
            // The row scrolls sideways on a phone: the server shown is brought into view.
            if (selected) button.BringIntoView();
        }
        UpdateTopLine();
    }

    /// <summary>The terminal shows while there is a session and nothing covers it; the key bar on a phone with it.</summary>
    private void UpdateTerminal()
    {
        if (_viewModel is null) return;
        var shown = _viewModel.ShowTerminal && _covering == 0;
        TerminalFrame.IsVisible = shown;
        KeyBar.IsVisible = shown && _narrow && _module?.IsEnabled != false;
    }

    /// <summary>A sheet opens over the page (or closes): the native terminal steps aside meanwhile.</summary>
    private void Covering(bool open)
    {
        _covering = Math.Max(0, _covering + (open ? 1 : -1));
        UpdateTerminal();
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _terminal?.Focus(), DispatcherPriority.Background);

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Picker.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
        UpdateTerminal();
    }

    // ---- Connecting -----------------------------------------------------------------------------------------------------

    private void Host_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SshHostRow row && _viewModel is not null) _viewModel.SelectedHost = row;
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e) => await ConnectAsync();

    private async void Password_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConnectAsync();
    }

    /// <summary>The password goes to this one connection and the box is emptied.</summary>
    private async Task ConnectAsync()
    {
        if (_viewModel is null) return;
        var password = PasswordBox.Text;
        PasswordBox.Text = "";
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

    // ---- The server's menu ------------------------------------------------------------------------------------------------

    private async void ServerMenu_Click(object? sender, RoutedEventArgs e) => await OpenMenuAsync();

    /// <summary>The server's menu as a sheet (read from the server the first time); a choice runs in the terminal.</summary>
    public async Task OpenMenuAsync()
    {
        if (_viewModel is null) return;
        if (_menu is not { IsOpen: true })
        {
            var menu = _viewModel.Menu;
            SheetHandle? sheet = null;
            sheet = Sheet.Show(this, new SshMenuSheet(menu, _viewModel, () => sheet?.Close()), 440);
            if (sheet is null) return;
            _menu = sheet;
            Covering(true);
            sheet.Closed += () =>
            {
                if (ReferenceEquals(_menu, sheet)) _menu = null;
                Covering(false);
                // A form left open goes back to the list for next time, unless a question about it is on screen.
                if (menu.IsFormOpen && !_viewModel.IsAskingUser) menu.BackCommand.Execute(null);
            };
        }
        try
        {
            await _viewModel.Menu.OpenAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _viewModel.Message = "The menu could not be read: " + ex.Message;
        }
    }

    /// <summary>The command is in the terminal: the menu closes and the keys go back there.</summary>
    private void OnMenuTyped(object? sender, EventArgs e)
    {
        _menu?.Close();
        Dispatcher.UIThread.Post(() => _terminal?.Focus(), DispatcherPriority.Background);
    }

    // ---- Settings -----------------------------------------------------------------------------------------------------

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    private void AddServer_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.NewHostCommand.Execute(null);
        OpenSettings(focusAddress: true);
    }

    private void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_host?.OpenSshFolder is { } folder) _viewModel?.ImportOpenSsh(folder);
    }

    /// <summary>The gear: the servers, this device's key, the known server keys and the terminal, as a sheet over the page.</summary>
    public SheetHandle? OpenSettings(bool focusAddress = false)
    {
        if (_viewModel is null || _module is null || _host is null) return null;
        _settings?.Close();
        SheetHandle? sheet = null;
        var view = new SshSettingsView(_viewModel, _module, _host, () => sheet?.Close());
        sheet = Sheet.Show(this, view, 560);
        if (sheet is null) return null;
        _settings = sheet;
        Covering(true);
        // Back from Vault (unlocked there): the Vault fields are read again.
        _viewModel.OnSettingsShown();
        sheet.Closed += () =>
        {
            if (ReferenceEquals(_settings, sheet)) _settings = null;
            Covering(false);
        };
        if (focusAddress) Dispatcher.UIThread.Post(view.FocusAddress, DispatcherPriority.Input);
        return sheet;
    }

    // ---- The key bar --------------------------------------------------------------------------------------------------

    private void Key_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string key) _terminal?.SendKey(key);
        RefocusTerminal();
    }

    private void Char_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string text) _viewModel?.Send(text);
        RefocusTerminal();
    }

    // Ctrl stays on until the next key: the keyboard goes back to the terminal for it.
    private void Ctrl_Click(object? sender, RoutedEventArgs e) => RefocusTerminal();

    /// <summary>Avalonia takes the focus for the tapped key after the tap is handled: the terminal gets it back a moment later.</summary>
    private void RefocusTerminal() => DispatcherTimer.RunOnce(() => _terminal?.Focus(), TimeSpan.FromMilliseconds(80));

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        if (_terminal is null) return;
        try
        {
            await _terminal.PasteAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_viewModel is not null) _viewModel.Message = "Pasting failed: " + ex.Message;
        }
    }

    private void Keyboard_Click(object? sender, RoutedEventArgs e) => _terminal?.Focus();

    private void Menu_Click(object? sender, RoutedEventArgs e) => _host?.OpenMenu?.Invoke(this);
}
