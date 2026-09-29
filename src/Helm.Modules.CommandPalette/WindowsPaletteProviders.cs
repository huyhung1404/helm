using System.Diagnostics;
using Helm.Core.Desktop;
using Helm.Core.Palette;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.CommandPalette;

/// <summary>Opens things as the signed-in user: Helm runs as administrator, Explorer does not.</summary>
internal static class ShellOpen
{
    public static void AsUser(string target, ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Target}", target);
        }
    }
}

/// <summary>The windows that are open: typing part of a title (or the app's name) switches to it.</summary>
internal sealed class OpenWindowsPaletteProvider(IWindowService windows, ISettingsStoreFactory settings, ILogger<OpenWindowsPaletteProvider> logger)
    : IPaletteProvider
{
    private const int MaxResults = 5;
    private readonly ISettingsStore<CommandPaletteSettings> _settings = settings.Get<CommandPaletteSettings>(CommandPaletteModule.ModuleId);

    public string? ModuleId => null;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        if (query.IsEmpty || !_settings.Current.IncludeWindows) return [];
        IReadOnlyList<WindowInfo> open;
        try
        {
            open = windows.GetAppWindows();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list the open windows");
            return [];
        }
        return open
            .Where(w => w.Title.Length > 0 && !w.IsCloaked)
            .Select(w => (w, score: query.Score(w.Title, w.ProcessName)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(MaxResults)
            .Select(x =>
            {
                var hwnd = x.w.Handle;
                return new PaletteItem(x.w.Title, $"Switch to {x.w.ProcessName}", PaletteKind.Window, x.score, () => windows.Activate(hwnd))
                    { IsExternal = true };
            })
            .ToList();
    }
}

/// <summary>A page of Windows Settings, with the words people type for it (English and Vietnamese).</summary>
public sealed record WindowsSettingsPage(string Name, string Uri, string Keywords);

/// <summary>Windows Settings pages: "bluetooth", "wifi", "âm thanh", "update"… open the page.</summary>
internal sealed class WindowsSettingsPaletteProvider(ISettingsStoreFactory settings, ILogger<WindowsSettingsPaletteProvider> logger) : IPaletteProvider
{
    private readonly ISettingsStore<CommandPaletteSettings> _settings = settings.Get<CommandPaletteSettings>(CommandPaletteModule.ModuleId);

    public static IReadOnlyList<WindowsSettingsPage> Pages { get; } =
    [
        new("Display", "ms-settings:display", "screen resolution brightness scale monitor màn hình độ sáng độ phân giải"),
        new("Night light", "ms-settings:nightlight", "blue light warm ánh sáng đêm"),
        new("Sound", "ms-settings:sound", "audio volume speaker microphone âm thanh loa micro"),
        new("Notifications", "ms-settings:notifications", "focus do not disturb thông báo"),
        new("Power & battery", "ms-settings:powersleep", "sleep battery power pin nguồn ngủ"),
        new("Storage", "ms-settings:storagesense", "disk space cleanup ổ đĩa dung lượng"),
        new("Multitasking", "ms-settings:multitasking", "snap windows desktops đa nhiệm"),
        new("Clipboard", "ms-settings:clipboard", "history paste bộ nhớ tạm"),
        new("About this PC", "ms-settings:about", "system info rename pc thông tin máy"),
        new("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth devices headphones mouse thiết bị tai nghe chuột"),
        new("Printers & scanners", "ms-settings:printers", "printer scanner máy in"),
        new("Mouse", "ms-settings:mousetouchpad", "pointer cursor chuột con trỏ"),
        new("Touchpad", "ms-settings:devices-touchpad", "trackpad gestures bàn di"),
        new("Wi-Fi", "ms-settings:network-wifi", "wifi wireless network mạng không dây"),
        new("Network & internet", "ms-settings:network-status", "ethernet internet proxy vpn mạng"),
        new("VPN", "ms-settings:network-vpn", "vpn"),
        new("Proxy", "ms-settings:network-proxy", "proxy"),
        new("Personalization", "ms-settings:personalization", "wallpaper theme colors cá nhân hóa hình nền"),
        new("Background", "ms-settings:personalization-background", "wallpaper hình nền"),
        new("Colors", "ms-settings:colors", "dark mode light mode accent màu chế độ tối"),
        new("Taskbar", "ms-settings:taskbar", "thanh tác vụ"),
        new("Start", "ms-settings:personalization-start", "start menu"),
        new("Installed apps", "ms-settings:appsfeatures", "uninstall programs gỡ cài đặt ứng dụng"),
        new("Default apps", "ms-settings:defaultapps", "browser default ứng dụng mặc định"),
        new("Startup apps", "ms-settings:startupapps", "startup khởi động"),
        new("Accounts", "ms-settings:yourinfo", "account microsoft tài khoản"),
        new("Sign-in options", "ms-settings:signinoptions", "password pin windows hello đăng nhập mật khẩu"),
        new("Date & time", "ms-settings:dateandtime", "clock time zone ngày giờ múi giờ"),
        new("Language & region", "ms-settings:regionlanguage", "language region format ngôn ngữ vùng"),
        new("Typing", "ms-settings:typing", "keyboard autocorrect bàn phím gõ"),
        new("Gaming", "ms-settings:gaming-gamemode", "game mode chế độ chơi game"),
        new("Accessibility", "ms-settings:easeofaccess", "text size narrator magnifier trợ năng cỡ chữ"),
        new("Privacy & security", "ms-settings:privacy", "permissions location camera quyền riêng tư"),
        new("Windows Security", "ms-settings:windowsdefender", "defender antivirus virus bảo mật"),
        new("Windows Update", "ms-settings:windowsupdate", "update updates cập nhật"),
        new("Recovery", "ms-settings:recovery", "reset restore khôi phục"),
        new("Developers", "ms-settings:developers", "developer mode sudo nhà phát triển"),
    ];

    public string? ModuleId => null;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        if (query.IsEmpty || !_settings.Current.IncludeWindowsSettings) return [];
        return Pages
            .Select(p => (p, score: query.Score(p.Name, p.Keywords)))
            .Where(x => x.score > 0)
            .Select(x =>
            {
                var uri = x.p.Uri;
                return new PaletteItem(x.p.Name, "Windows Settings", PaletteKind.WindowsSetting, x.score, () => ShellOpen.AsUser(uri, logger))
                    { IsExternal = true };
            })
            .ToList();
    }
}

/// <summary>"Search the web for …", always last: the typed text in the chosen search engine.</summary>
internal sealed class WebSearchPaletteProvider(ISettingsStoreFactory settings, ILogger<WebSearchPaletteProvider> logger) : IPaletteProvider
{
    /// <summary>Below everything else, even external results.</summary>
    private const double Score = 0.01;

    private readonly ISettingsStore<CommandPaletteSettings> _settings = settings.Get<CommandPaletteSettings>(CommandPaletteModule.ModuleId);

    public string? ModuleId => null;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        var s = _settings.Current;
        if (query.Text.Length < 2 || !s.IncludeWebSearch || CommandPaletteSettings.WebSearchUrl(s.WebSearch, query.Text) is not { } url) yield break;
        yield return new PaletteItem($"Search the web for “{query.Text}”", CommandPaletteSettings.WebSearchNames[(int)s.WebSearch], PaletteKind.Web,
            Score, () => ShellOpen.AsUser(url, logger)) { IsExternal = true };
    }
}
