using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Ui.Windows;

namespace Helm.App.Shell;

/// <summary>
/// A notification: a small glass card in the bottom-right corner over every app (the tray icon Avalonia gives has no
/// balloons). It never takes the keyboard, goes away after a while or on its ✕, and a click on it runs its action. One
/// at a time: a new one replaces the one shown, as Windows' balloons did.
/// </summary>
internal static class NotificationCard
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(8);
    private static AvaloniaCornerWindow? _shown;
    private static DispatcherTimer? _timer;

    /// <summary>Set once by the app: the window and monitor services the corner window uses.</summary>
    public static (IWindowService Windows, IMonitorService Monitors)? Services { get; set; }

    public static void Show(string title, string message, Action? onClick)
    {
        if (Services is not { } services) return;
        Close();
        var window = new AvaloniaCornerWindow(services.Windows, services.Monitors, title, 380, null, _ => { });
        var close = new Button { Classes = { "flat" }, Width = 28, Height = 28, Padding = new Thickness(0), Content = "✕",
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(close, "Dismiss");
        var card = new Border
        {
            Classes = { "sheet" },
            Margin = new Thickness(AvaloniaCornerWindow.ShadowSpace),
            Padding = new Thickness(16, 12, 8, 14),
            CornerRadius = new CornerRadius(16),
            Cursor = onClick is null ? Cursor.Default : new Cursor(StandardCursorType.Hand),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = "HELM", Classes = { "eyebrow" } },
                            new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = message, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, MaxLines = 4,
                                TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                    close,
                },
            },
        };
        Grid.SetColumn(close, 1);
        close.Click += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        card.PointerReleased += (_, _) =>
        {
            Close();
            onClick?.Invoke();
        };
        // Under the mouse it stays; it goes once the mouse leaves and the time is up.
        card.PointerEntered += (_, _) => _timer?.Stop();
        card.PointerExited += (_, _) => _timer?.Start();
        window.Content = new ThemeVariantScope { Child = card };
        _shown = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shown, window)) _shown = null;
        };
        window.Show();
        _timer = new DispatcherTimer { Interval = ShowFor };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    private static void Close()
    {
        _timer?.Stop();
        _timer = null;
        var shown = _shown;
        _shown = null;
        shown?.Close();
    }
}
