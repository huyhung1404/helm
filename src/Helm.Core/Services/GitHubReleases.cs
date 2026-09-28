using System.Text.Json.Serialization;
using Helm.Core.Settings;

namespace Helm.Core.Services;

/// <summary>The fields Helm reads from GitHub's "list releases" API.</summary>
public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

/// <param name="Digest">"sha256:&lt;hex&gt;" on assets uploaded since mid-2025; null on older ones.</param>
public sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("digest")] string? Digest);

/// <summary>A release that carries the asset this platform installs.</summary>
public sealed record ReleaseCandidate(string Version, string? Notes, GitHubAsset Asset)
{
    /// <summary>Lower-case hex SHA-256 of the asset, when GitHub published one.</summary>
    public string? Sha256 => Asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..].ToLowerInvariant() : null;
}

/// <summary>
/// Picks the update for platforms without Velopack (Android): the newest release on the chosen channel that has the
/// platform's asset (e.g. Helm-android.apk) and is newer than what is running. Pure, so it is unit-tested.
/// </summary>
public static class GitHubReleases
{
    public const string ApiUrl = "https://api.github.com/repos/huyhung1404/helm/releases?per_page=30";

    public static ReleaseCandidate? PickUpdate(IEnumerable<GitHubRelease> releases, UpdateChannel channel, string currentVersion, string assetName)
    {
        ReleaseCandidate? best = null;
        foreach (var release in releases)
        {
            if (release.Draft) continue;
            if (release.Prerelease && !UpdatePolicy.IncludePrereleases(channel)) continue;
            if (UpdatePolicy.VersionFromTag(release.TagName) is not { } version) continue;
            var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null) continue;
            if (!UpdatePolicy.IsNewer(version, currentVersion)) continue;
            if (best is null || UpdatePolicy.IsNewer(version, best.Version)) best = new ReleaseCandidate(version, release.Body, asset);
        }
        return best;
    }
}
