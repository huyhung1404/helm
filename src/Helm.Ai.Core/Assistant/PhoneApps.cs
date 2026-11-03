using Helm.Core.Text;

namespace Helm.Ai.Assistant;

/// <summary>
/// The app the user named among the apps of the phone's launcher ("grab" → Grab, "you tube" → YouTube), for
/// <see cref="PhoneToolSpecs.OpenApp"/>. Platform-free, so the phone's tool only lists its apps.
/// </summary>
public static class PhoneApps
{
    public sealed record App(string Label, string Package);

    /// <summary>
    /// The apps that fit <paramref name="wanted"/>, best kind of match only: the same name (accents, case and spaces
    /// aside), else names with a word starting with it, else names containing it, else the package's name. Empty: none.
    /// </summary>
    public static IReadOnlyList<App> Match(string wanted, IEnumerable<App> apps)
    {
        var name = Squash(Strip(TextSearch.Fold(wanted)));
        if (name.Length == 0) return [];
        var all = apps.DistinctBy(a => a.Package).Select(a => (App: a, Folded: TextSearch.Fold(a.Label))).ToList();
        Func<(App App, string Folded), bool>[] kinds =
        [
            a => Squash(a.Folded) == name,
            a => a.Folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(name, StringComparison.Ordinal)) || Squash(a.Folded).StartsWith(name, StringComparison.Ordinal),
            a => Squash(a.Folded).Contains(name, StringComparison.Ordinal),
            a => a.App.Package.Replace(".", "", StringComparison.Ordinal).Contains(name, StringComparison.OrdinalIgnoreCase),
        ];
        foreach (var kind in kinds)
            if (all.Where(kind).Select(a => a.App).OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList() is { Count: > 0 } found)
                return found;
        return [];
    }

    /// <summary>"app Grab", "ứng dụng YouTube": the words around the name are not part of it.</summary>
    private static string Strip(string folded)
    {
        foreach (var prefix in new[] { "ung dung ", "app ", "mo " })
            if (folded.StartsWith(prefix, StringComparison.Ordinal)) folded = folded[prefix.Length..];
        return folded;
    }

    private static string Squash(string folded) => new([.. folded.Where(char.IsLetterOrDigit)]);
}

/// <summary>Directions to a place in the phone's maps app (<see cref="PhoneToolSpecs.Navigate"/>), as links it opens.</summary>
public static class PhoneMaps
{
    /// <summary>
    /// Google Maps' turn-by-turn link (motorbike: its two-wheeler mode); null for transit, which it does not take.
    /// </summary>
    public static string? Navigation(string destination, string? mode) => (mode ?? "motorbike") switch
    {
        "transit" => null,
        var m => $"google.navigation:q={Uri.EscapeDataString(destination.Trim())}&mode={m switch { "car" => "d", "walk" => "w", _ => "l" }}",
    };

    /// <summary>The directions as a web link: any maps app (or the browser) opens it.</summary>
    public static string Directions(string destination, string? mode) =>
        $"https://www.google.com/maps/dir/?api=1&destination={Uri.EscapeDataString(destination.Trim())}&travelmode={mode switch { "walk" => "walking", "transit" => "transit", _ => "driving" }}";
}
