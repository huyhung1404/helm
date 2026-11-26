using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.Ssh.Ui;

/// <summary>The server's menu as a sheet (see the XAML).</summary>
public partial class SshMenuSheet : UserControl
{
    private readonly SshMenuViewModel? _menu;
    private readonly SshViewModel? _viewModel;
    private readonly Action? _close;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public SshMenuSheet() => InitializeComponent();

    public SshMenuSheet(SshMenuViewModel menu, SshViewModel viewModel, Action close)
    {
        _menu = menu;
        _viewModel = viewModel;
        _close = close;
        DataContext = menu;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
    }

    private async void Item_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MenuEntry entry || _menu is null) return;
        try
        {
            await _menu.ChooseAsync(entry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_viewModel is not null) _viewModel.Message = "Could not run: " + ex.Message;
            _close?.Invoke();
        }
    }
}
