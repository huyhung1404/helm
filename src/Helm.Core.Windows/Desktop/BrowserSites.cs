using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Desktop;

/// <summary>
/// The web site a browser window shows, as a key that stands for the site ("facebook.com"), for anything that wants to
/// remember something per site rather than per app. The URL is read from the address bar through UI Automation, the way
/// Vault auto-type does, and cut down to the registrable domain so every page of a site means the same.
/// </summary>
public interface IBrowserSites
{
    /// <summary>True when <paramref name="process"/> is a web browser whose address Helm knows how to read.</summary>
    bool IsBrowser(string process);

    /// <summary>
    /// The site key for a browser window ("facebook.com"), or null when it is not a browser, the address cannot be read,
    /// or the page is not a web page (a new tab, a settings page, a local file).
    /// </summary>
    string? SiteKey(nint hwnd, string process);

    /// <summary>
    /// What the browser's address bar shows ("github.com/login"), with the full host and path, or null when the window
    /// is not a browser or its address cannot be read. For callers that need the whole URL, not just the site.
    /// </summary>
    string? Address(nint hwnd, string process);
}

public sealed class BrowserSites(ILogger<BrowserSites> logger) : IBrowserSites
{
    private static readonly string[] s_chromium = ["chrome", "msedge", "brave", "opera", "vivaldi", "browser", "arc", "thorium", "chromium"];
    private static readonly string[] s_firefox = ["firefox", "librewolf", "waterfox", "floorp", "zen"];

    public bool IsBrowser(string process) => Kind(process) != BrowserKind.None;

    public string? SiteKey(nint hwnd, string process)
    {
        var kind = Kind(process);
        if (kind == BrowserKind.None) return null;
        return RegistrableDomain(ReadAddress(hwnd, kind, process));
    }

    public string? Address(nint hwnd, string process)
    {
        var kind = Kind(process);
        return kind == BrowserKind.None ? null : ReadAddress(hwnd, kind, process);
    }

    private static BrowserKind Kind(string process)
    {
        var name = process.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.ToLowerInvariant();
        if (s_firefox.Contains(name)) return BrowserKind.Firefox;
        return s_chromium.Contains(name) ? BrowserKind.Chromium : BrowserKind.None;
    }

    /// <summary>What the browser's address bar shows ("github.com/login"), or null when it cannot be read.</summary>
    private string? ReadAddress(nint hwnd, BrowserKind kind, string process)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            AutomationElement? bar = kind == BrowserKind.Firefox
                ? root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"))
                // Chromium: the first edit box of the window is the address bar (the page's own come later).
                : root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            return bar?.GetCurrentPropertyValue(ValuePattern.ValueProperty) as string is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            logger.LogDebug(ex, "Could not read the address of {Process}", process);
            return null;
        }
    }

    private enum BrowserKind { None, Chromium, Firefox }

    // The two-level public suffixes people type in Vietnam and the common ones worldwide, so "bbc.co.uk" and
    // "example.com.vn" give "bbc.co.uk" and "example.com.vn", not "co.uk" / "com.vn". Not a full Public Suffix List:
    // a key off by a label only means a rarely seen site keeps its own language, which is harmless here.
    private static readonly HashSet<string> s_twoLevelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk", "ltd.uk", "plc.uk",
        "com.vn", "net.vn", "org.vn", "edu.vn", "gov.vn", "io.vn",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "com.hk", "com.tw",
        "com.au", "net.au", "org.au", "gov.au", "edu.au", "co.jp", "or.jp", "ne.jp", "co.kr", "or.kr",
        "com.br", "com.mx", "com.sg", "com.my", "com.ph", "co.id", "co.th", "com.tr", "co.in", "co.za", "co.nz",
    };

    /// <summary>
    /// "https://www.Example.co.uk:443/login" → "example.co.uk"; the registrable domain, so every page of a site keys
    /// the same. Null when the address is not a web page (a new tab, settings, a local file, an IP address).
    /// </summary>
    public static string? RegistrableDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        // The browser's own pages and anything that is not plain http(s): no site of their own.
        if (text.Contains("://", StringComparison.Ordinal))
        {
            var scheme = text[..text.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
            if (scheme is not ("http" or "https")) return null;
        }
        else
        {
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        // No dot (localhost, intranet names) or a bare IP address: nothing site-like to remember.
        if (!host.Contains('.') || System.Net.IPAddress.TryParse(host, out _)) return null;

        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var lastTwo = labels[^2] + "." + labels[^1];
        var keep = s_twoLevelSuffixes.Contains(lastTwo) ? 3 : 2;
        return keep >= labels.Length ? host : string.Join('.', labels[^keep..]);
    }
}
