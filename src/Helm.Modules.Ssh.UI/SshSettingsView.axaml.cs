using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Ssh.Ui;

/// <summary>SSH's settings, shown as a sheet over the page (see <see cref="SshView"/>'s gear).</summary>
public partial class SshSettingsView : UserControl
{
    private readonly SshViewModel? _viewModel;
    private readonly ISshPageHost? _host;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public SshSettingsView() => InitializeComponent();

    public SshSettingsView(SshViewModel viewModel, IModule module, ISshPageHost host, Action close)
    {
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        // Key files only where they can be read (the PC); the choice is left out of the list elsewhere.
        viewModel.AllowKeyFiles = host.CanUseKeyFiles;
        InitializeComponent();
        PlatformCards.Content = host.CreatePlatformSettings();
        KeyHint.Text = "An Ed25519 key made for this device. " + host.KeyProtection;
        BrowseButton.IsVisible = host.CanUseKeyFiles;
        // Import only on the PC: "Add a server" then fills the row.
        ImportButton.IsVisible = host.OpenSshFolder is not null;
        if (host.OpenSshFolder is null)
        {
            Grid.SetColumn(AddButton, 0);
            Grid.SetColumnSpan(AddButton, 2);
        }
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        }
        AttachedToVisualTree += (_, _) => module.PropertyChanged += OnModule;
        DetachedFromVisualTree += (_, _) =>
        {
            module.PropertyChanged -= OnModule;
            // A form left open is dropped when the sheet closes.
            if (viewModel.IsEditing) viewModel.CancelEditCommand.Execute(null);
        };
    }

    /// <summary>Import and Add share a row when they fit; on a phone each has its own.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!ImportButton.IsVisible) return;
        var stack = e.NewSize.Width < 440;
        AddButtons.ColumnDefinitions = ColumnDefinitions.Parse(stack ? "*" : "*,*");
        AddButtons.RowDefinitions = RowDefinitions.Parse(stack ? "Auto,Auto" : "Auto");
        AddButtons.RowSpacing = 8;
        Grid.SetColumn(AddButton, stack ? 0 : 1);
        Grid.SetRow(AddButton, stack ? 1 : 0);
    }

    /// <summary>The cursor in the new server's address (the first run's one step).</summary>
    public void FocusAddress() => AddressBox.Focus();

    private void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_host?.OpenSshFolder is { } folder) _viewModel?.ImportOpenSsh(folder);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (_host is null || _viewModel is null) return;
        if (await _host.PickKeyFileAsync() is { } path) _viewModel.EditKeyFile = path;
    }
}
