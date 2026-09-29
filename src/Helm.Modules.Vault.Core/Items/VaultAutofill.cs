using Helm.Core.Text;

namespace Helm.Modules.Vault.Items;

/// <summary>What asks for a login: a web page (its host) or an app (its package on Android, its window on Windows).</summary>
/// <param name="Host">The page's host ("accounts.google.com"); null for an app.</param>
/// <param name="AppId">Android package ("com.facebook.katana") or Windows process name ("slack").</param>
/// <param name="Title">A window title (Windows), used when nothing else matches.</param>
public sealed record AutofillTarget(string? Host, string? AppId = null, string? Title = null);

/// <summary>One login offered for a target, best first.</summary>
public sealed record AutofillMatch(VaultEntry Entry, int Score)
{
    public string Title => Entry.Item.Title;

    public string? Username => Entry.Item.Username;

    public string? Password => Entry.Item.Password;
}

/// <summary>
/// Which vault logins fit a site or an app, for Android autofill and Windows auto-type. A login's Website fields decide:
/// the same site (www. and sub-sites of the saved one count), or an app remembered as "androidapp://package" (Android) or
/// "app://process" (Windows). Nothing is ever matched by a look-alike name: "paypa1.com" never gets PayPal's password.
/// </summary>
public static class VaultAutofill
{
    public const string AndroidAppScheme = "androidapp://";
    public const string WindowsAppScheme = "app://";

    /// <summary>The logins for <paramref name="target"/>, best first (exact host, then parent domain, then remembered app).</summary>
    public static IReadOnlyList<AutofillMatch> Match(IEnumerable<VaultEntry> entries, AutofillTarget target)
    {
        var host = NormalizeHost(target.Host);
        var app = target.AppId?.Trim().ToLowerInvariant();
        var matches = new List<AutofillMatch>();
        foreach (var entry in entries)
        {
            if (entry.Trashed || entry.Item.Kind != VaultItemKind.Login || entry.Item.Password is not { Length: > 0 }) continue;
            var best = 0;
            foreach (var url in entry.Item.Fields.Where(f => f.Kind == VaultFieldKind.Url).Select(f => f.Value.Trim()))
            {
                if (app is { Length: > 0 } && (IsApp(url, AndroidAppScheme, app) || IsApp(url, WindowsAppScheme, app))) best = Math.Max(best, 90);
                if (host is null || HostOf(url) is not { } saved) continue;
                if (saved == host) best = Math.Max(best, 100);
                else if (host.EndsWith("." + saved, StringComparison.Ordinal)) best = Math.Max(best, 80); // login.example.com for example.com
                else if (saved.EndsWith("." + host, StringComparison.Ordinal)) best = Math.Max(best, 60);  // saved app.example.com, page example.com
            }
            if (best > 0) matches.Add(new AutofillMatch(entry, best + (entry.Item.Favorite ? 1 : 0)));
        }
        return matches.OrderByDescending(m => m.Score).ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Windows only, when a window gave no site or remembered app: logins whose title or saved site name appears in the
    /// window title ("GitHub" in "Sign in to GitHub · GitHub - Google Chrome"). The picker shows them; nothing is typed
    /// without the user choosing when this is all there is.
    /// </summary>
    public static IReadOnlyList<AutofillMatch> Suggest(IEnumerable<VaultEntry> entries, string? windowTitle)
    {
        var title = TextSearch.Fold(windowTitle);
        if (title.Length == 0) return [];
        return entries
            .Where(e => !e.Trashed && e.Item.Kind == VaultItemKind.Login && e.Item.Password is { Length: > 0 })
            .Select(e => (e, name: TextSearch.Fold(e.Item.Title), sites: e.Item.Fields.Where(f => f.Kind == VaultFieldKind.Url).Select(f => SiteName(f.Value)).OfType<string>()))
            .Where(x => (x.name.Length >= 3 && title.Contains(x.name, StringComparison.Ordinal)) || x.sites.Any(s => s.Length >= 3 && title.Contains(s, StringComparison.Ordinal)))
            .Select(x => new AutofillMatch(x.e, 10))
            .ToList();
    }

    /// <summary>"https://www.Example.com:443/login" → "example.com"; null when it is not a web address.</summary>
    public static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        if (text.StartsWith(AndroidAppScheme, StringComparison.OrdinalIgnoreCase) || text.StartsWith(WindowsAppScheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.') ? NormalizeHost(uri.Host) : null;
    }

    /// <summary>
    /// After the user picked a login for an app by hand: adds "androidapp://package" (or "app://process") to it, so it
    /// is offered there next time. False when it was remembered already.
    /// </summary>
    public static bool RememberApp(VaultStore store, string uid, string scheme, string appId)
    {
        if (store.Get(uid) is not { } entry) return false;
        var url = AppUrl(scheme, appId);
        if (entry.Item.Fields.Any(f => f.Kind == VaultFieldKind.Url && string.Equals(f.Value.Trim(), url, StringComparison.OrdinalIgnoreCase))) return false;
        store.Save(uid, entry.Item with { Fields = [.. entry.Item.Fields, new VaultField("App", url, VaultFieldKind.Url)] });
        return true;
    }

    /// <summary>The field value that remembers an app for a login.</summary>
    public static string AppUrl(string scheme, string appId) => scheme + appId.Trim().ToLowerInvariant();

    private static bool IsApp(string url, string scheme, string app) =>
        url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(url[scheme.Length..].Trim().TrimEnd('/'), app, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    /// <summary>"accounts.google.com" → "google"; the part people read as the site's name.</summary>
    private static string? SiteName(string url)
    {
        if (HostOf(url) is not { } host) return null;
        var parts = host.Split('.');
        return parts.Length >= 2 ? parts[^2] : parts[0];
    }
}

/// <summary>What kind of input a form field is, from what the platform says about it (Android view node, HTML input).</summary>
public enum AutofillFieldKind
{
    None,
    Username,
    Password,
    /// <summary>A one-time code input (the 2FA step).</summary>
    OneTimeCode,
}

/// <summary>
/// Recognises login fields from plain facts, so the rules are the same (and tested) whatever reads the form: autofill
/// hints, whether the input hides its text, HTML type/name/id/autocomplete, and the field's id and hint text.
/// </summary>
public static class AutofillFieldClassifier
{
    private static readonly string[] UserWords = ["user", "login", "email", "e-mail", "account", "phone", "mobile", "tài khoản", "tai khoan", "đăng nhập"];
    private static readonly string[] PasswordWords = ["password", "passwd", "pwd", "pass", "mật khẩu", "mat khau"];
    private static readonly string[] CodeWords = ["otp", "one-time", "2fa", "totp", "verification code", "security code", "mã xác", "ma xac"];

    public static AutofillFieldKind Classify(IReadOnlyCollection<string>? hints, bool hidesText, string? htmlType, string? htmlName, string? htmlAutocomplete,
        string? idEntry, string? hintText)
    {
        foreach (var hint in hints ?? [])
        {
            var h = hint.ToLowerInvariant();
            if (h.Contains("password")) return h.Contains("otp") || h.Contains("sms") ? AutofillFieldKind.OneTimeCode : AutofillFieldKind.Password;
            if (h is "username" or "emailaddress" or "email" or "phone" or "phonenumber" || h.Contains("username") || h.Contains("email")) return AutofillFieldKind.Username;
            if (h.Contains("otp") || h.Contains("smsotp") || h.Contains("2fa")) return AutofillFieldKind.OneTimeCode;
        }
        var autocomplete = htmlAutocomplete?.ToLowerInvariant() ?? "";
        if (autocomplete.Contains("one-time-code")) return AutofillFieldKind.OneTimeCode;
        if (autocomplete.Contains("current-password") || autocomplete.Contains("new-password")) return AutofillFieldKind.Password;
        if (autocomplete.Contains("username") || autocomplete.Contains("email")) return AutofillFieldKind.Username;

        var type = htmlType?.ToLowerInvariant();
        if (hidesText || type == "password") return AutofillFieldKind.Password;
        if (type is not (null or "" or "text" or "email" or "tel" or "number")) return AutofillFieldKind.None; // checkbox, submit, search, hidden…

        var words = TextSearch.Fold(string.Join(' ', new[] { htmlName, idEntry, hintText }.Where(s => !string.IsNullOrWhiteSpace(s))));
        if (words.Length == 0) return type == "email" ? AutofillFieldKind.Username : AutofillFieldKind.None;
        if (CodeWords.Any(w => words.Contains(TextSearch.Fold(w), StringComparison.Ordinal))) return AutofillFieldKind.OneTimeCode;
        if (PasswordWords.Any(w => words.Contains(TextSearch.Fold(w), StringComparison.Ordinal))) return AutofillFieldKind.Password;
        if (type == "email" || UserWords.Any(w => words.Contains(TextSearch.Fold(w), StringComparison.Ordinal))) return AutofillFieldKind.Username;
        return AutofillFieldKind.None;
    }
}
