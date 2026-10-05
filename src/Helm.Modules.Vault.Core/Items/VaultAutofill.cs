using Helm.Core.Text;

namespace Helm.Modules.Vault.Items;

/// <summary>What asks for a login: a web page (its host) or an app (its package on Android, its window on Windows).</summary>
/// <param name="Host">The page's host ("accounts.google.com"); null for an app.</param>
/// <param name="AppId">Android package ("com.facebook.katana") or Windows process name ("slack").</param>
/// <param name="Title">A window title (Windows), used when nothing else matches.</param>
/// <param name="AppCert">
/// Android: the SHA-256 of the asking app's signing certificate (<see cref="VaultAutofill.AppUrl"/>); null when it
/// could not be read. An app with the same package name but another signature is another app.
/// </param>
public sealed record AutofillTarget(string? Host, string? AppId = null, string? Title = null, string? AppCert = null);

/// <summary>One login offered for a target, best first.</summary>
/// <param name="Verified">
/// False for an Android app remembered before Helm kept its signing certificate: it is not offered in one tap, only in
/// the picker, where choosing it records the certificate.
/// </param>
public sealed record AutofillMatch(VaultEntry Entry, int Score, bool Verified = true)
{
    public string Title => Entry.Item.Title;

    public string? Username => Entry.Item.Username;

    public string? Password => Entry.Item.Password;
}

/// <summary>
/// Which vault logins fit a site or an app, for Android autofill and Windows auto-type. A login's Website fields decide:
/// the same site (www. and sub-sites of the saved one count), or an app remembered as "androidapp://package" (Android) or
/// "app://process" (Windows). Nothing is ever matched by a look-alike name: "paypa1.com" never gets PayPal's password.
/// An Android app is remembered with its signing certificate ("androidapp://package#sha256"), so another app that took
/// the same package name (a fake one installed from a file) is never offered the login.
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
            int best = 0, unverified = 0;
            foreach (var url in entry.Item.Fields.Where(f => f.Kind == VaultFieldKind.Url).Select(f => f.Value.Trim()))
            {
                if (app is { Length: > 0 })
                {
                    if (IsApp(url, WindowsAppScheme, app)) best = Math.Max(best, 90);
                    else if (AndroidAppOf(url) is { } saved && saved.Package == app)
                    {
                        // Remembered with a certificate: only that signature. Without one (older Helm): the picker only.
                        if (saved.Cert is null) unverified = 90;
                        else if (target.AppCert is { } cert && string.Equals(saved.Cert, cert, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 90);
                    }
                }
                if (host is null || HostOf(url) is not { } savedHost) continue;
                if (savedHost == host) best = Math.Max(best, 100);
                else if (host.EndsWith("." + savedHost, StringComparison.Ordinal)) best = Math.Max(best, 80); // login.example.com for example.com
                else if (savedHost.EndsWith("." + host, StringComparison.Ordinal)) best = Math.Max(best, 60);  // saved app.example.com, page example.com
            }
            var favorite = entry.Item.Favorite ? 1 : 0;
            if (best > 0) matches.Add(new AutofillMatch(entry, best + favorite));
            else if (unverified > 0) matches.Add(new AutofillMatch(entry, unverified + favorite, Verified: false));
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
    /// After the user picked a login for an app by hand: adds "androidapp://package#cert" (or "app://process") to it, so
    /// it is offered there next time. An Android app already remembered without a certificate, or with another one,
    /// is updated in place. False when it was remembered already.
    /// </summary>
    public static bool RememberApp(VaultStore store, string uid, string scheme, string appId, string? appCert = null)
    {
        if (store.Get(uid) is not { } entry) return false;
        var url = AppUrl(scheme, appId, appCert);
        var fields = entry.Item.Fields;
        if (fields.Any(f => f.Kind == VaultFieldKind.Url && string.Equals(f.Value.Trim(), url, StringComparison.OrdinalIgnoreCase))) return false;
        var package = appId.Trim().ToLowerInvariant();
        var same = scheme == AndroidAppScheme
            ? fields.FirstOrDefault(f => f.Kind == VaultFieldKind.Url && AndroidAppOf(f.Value.Trim())?.Package == package)
            : null;
        store.Save(uid, entry.Item with
        {
            Fields = same is null ? [.. fields, new VaultField("App", url, VaultFieldKind.Url)] : [.. fields.Select(f => ReferenceEquals(f, same) ? f with { Value = url } : f)],
        });
        return true;
    }

    /// <summary>The field value that remembers an app for a login; an Android app with its certificate when known.</summary>
    public static string AppUrl(string scheme, string appId, string? appCert = null) =>
        scheme + appId.Trim().ToLowerInvariant() + (scheme == AndroidAppScheme && appCert is { Length: > 0 } ? "#" + appCert.Trim().ToLowerInvariant() : "");

    private static bool IsApp(string url, string scheme, string app) =>
        url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(url[scheme.Length..].Trim().TrimEnd('/'), app, StringComparison.OrdinalIgnoreCase);

    /// <summary>"androidapp://com.example#ab12…" → (package, certificate or null); null for anything else.</summary>
    private static (string Package, string? Cert)? AndroidAppOf(string url)
    {
        if (!url.StartsWith(AndroidAppScheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[AndroidAppScheme.Length..].Trim();
        var hash = rest.IndexOf('#');
        var package = (hash < 0 ? rest : rest[..hash]).Trim().TrimEnd('/').ToLowerInvariant();
        var cert = hash < 0 ? "" : rest[(hash + 1)..].Trim();
        return (package, cert.Length > 0 ? cert : null);
    }

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
