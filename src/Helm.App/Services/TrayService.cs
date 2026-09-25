using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Helm.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Helm.App.Services;

/// <summary>Tray icon: left click opens Helm; menu has Open, per-module toggles and Exit.</summary>
internal sealed class TrayService(IModuleHost modules, ILogger<TrayService> logger) : IDisposable
{
    private TaskbarIcon? _icon;
    private Action? _open;
    private Action? _exit;

    public void Create(Action open, Action exit)
    {
        _open = open;
        _exit = exit;
        try
        {
            _icon = new TaskbarIcon
            {
                ToolTipText = "Helm",
                IconSource = TrayIconSource(),
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(open),
                ContextMenu = BuildMenu(),
            };
            _icon.ForceCreate(enablesEfficiencyMode: false);        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tray icon");
        }
    }

    /// <summary>Rebuilds the menu (call once modules are registered/started).</summary>
    public void RefreshMenu()
    {
        if (_icon is not null) _icon.ContextMenu = BuildMenu();
    }

    private (string Version, Action Apply)? _updateReady;

    /// <summary>Adds (or removes, with null) a "Restart to update" item to the tray menu.</summary>
    public void SetUpdateReady(string? version, Action? apply)
    {
        _updateReady = version is not null && apply is not null ? (version, apply) : null;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshMenu);
    }

    public void ShowNotification(string title, string message)
    {
        try { _icon?.ShowNotification(title, message); }
        catch (Exception ex) { logger.LogWarning(ex, "Tray notification failed"); }
    }

    /// <summary>The color mark reads well on both light and dark taskbars.</summary>
    private static BitmapImage TrayIconSource() => new(new Uri("pack://application:,,,/Assets/helm.ico"));

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open Helm", FontWeight = System.Windows.FontWeights.SemiBold };
        open.Click += (_, _) => _open?.Invoke();
        menu.Items.Add(open);

        if (modules.Modules.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var module in modules.Modules)
            {
                var item = new MenuItem { Header = module.DisplayName, IsCheckable = true };
                item.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(IHelmModule.IsEnabled)) { Source = module, Mode = BindingMode.TwoWay });
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        if (_updateReady is { } ready)
        {
            var update = new MenuItem { Header = $"Restart to update (v{ready.Version})" };
            update.Click += (_, _) => ready.Apply();
            menu.Items.Add(update);
        }
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => _exit?.Invoke();
        menu.Items.Add(exit);
        return menu;
    }
}
