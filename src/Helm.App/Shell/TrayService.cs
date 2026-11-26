using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.App.Shell;

/// <summary>
/// Helm's icon in the tray: a click opens Helm; its menu has Open, a switch per tool, Report a bug, Restart to update
/// and Exit. Notifications are Helm's own cards in the corner of the screen (<see cref="NotificationCard"/>): the tray
/// icon Avalonia gives has no balloons.
/// </summary>
internal sealed class TrayService(IModuleHost modules, ILogger<TrayService> logger) : IUserNotifications, IDisposable
{
    private TrayIcon? _icon;
    private Action? _open;
    private Action? _exit;
    private (string Version, Action Apply)? _updateReady;

    /// <summary>The menu's "Report a bug…" (set once bug reports start; the menu is rebuilt with the tools).</summary>
    public Action? ReportBug { get; set; }

    public void Create(Action open, Action exit)
    {
        _open = open;
        _exit = exit;
        try
        {
            _icon = new TrayIcon
            {
                ToolTipText = "Helm",
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Helm/Assets/helm.ico"))),
                Menu = BuildMenu(),
            };
            _icon.Clicked += (_, _) => _open?.Invoke();
            if (Application.Current is { } app) TrayIcon.SetIcons(app, [_icon]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tray icon");
        }
    }

    /// <summary>Rebuilds the menu (call once the tools are registered and started).</summary>
    public void RefreshMenu()
    {
        if (_icon is not null) _icon.Menu = BuildMenu();
    }

    /// <summary>Adds (or removes, with null) a "Restart to update" item to the tray menu.</summary>
    public void SetUpdateReady(string? version, Action? apply)
    {
        _updateReady = version is not null && apply is not null ? (version, apply) : null;
        Dispatcher.UIThread.Post(RefreshMenu);
    }

    public void ShowNotification(string title, string message) => Show(title, message);

    public void Show(string title, string message, Action? onClick = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                NotificationCard.Show(title, message, onClick);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification failed");
            }
        });

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.IsVisible = false;
        _icon.Dispose();
        _icon = null;
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open Helm");
        open.Click += (_, _) => _open?.Invoke();
        menu.Items.Add(open);

        if (modules.Modules.Count > 0)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            foreach (var module in modules.Modules.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new NativeMenuItem(module.DisplayName) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = module.IsEnabled };
                item.Click += (_, _) => module.IsEnabled = !module.IsEnabled;
                module.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => item.IsChecked = module.IsEnabled);
                };
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        if (ReportBug is { } report)
        {
            var bug = new NativeMenuItem("Report a bug…  (Ctrl+Alt+F12)");
            bug.Click += (_, _) => report();
            menu.Items.Add(bug);
        }
        if (_updateReady is { } ready)
        {
            var update = new NativeMenuItem($"Restart to update (v{ready.Version})");
            update.Click += (_, _) => ready.Apply();
            menu.Items.Add(update);
        }
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => _exit?.Invoke();
        menu.Items.Add(exit);
        return menu;
    }
}
