using System.Text.Json;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>The platform-neutral parts of the Android app's updater and secret storage.</summary>
public class AndroidUpdateTests
{
    private const string Apk = "Helm-android.apk";

    private static GitHubRelease Release(string tag, bool prerelease = false, bool draft = false, params string[] assets) =>
        new(tag, draft, prerelease, $"Notes for {tag}",
            (assets.Length == 0 ? [Apk, "Helm-win-Setup.exe"] : assets).Select(a => new GitHubAsset(a, 1234, $"https://example.test/{tag}/{a}", null)).ToList());

    [Fact]
    public void Picks_the_newest_stable_release_that_has_the_apk()
    {
        var releases = new[] { Release("v0.7.0"), Release("v0.9.0", assets: "Helm-win-Setup.exe"), Release("v0.8.1"), Release("v0.8.0") };

        var pick = GitHubReleases.PickUpdate(releases, UpdateChannel.Stable, "0.7.0", Apk);

        Assert.NotNull(pick);
        Assert.Equal("0.8.1", pick.Version); // 0.9.0 has no APK (a Windows-only release)
        Assert.Equal("Notes for v0.8.1", pick.Notes);
        Assert.Equal("https://example.test/v0.8.1/Helm-android.apk", pick.Asset.DownloadUrl);
    }

    [Fact]
    public void Pre_releases_only_on_the_preview_channel()
    {
        var releases = new[] { Release("v0.8.0-preview.2", prerelease: true), Release("v0.7.1") };

        Assert.Equal("0.7.1", GitHubReleases.PickUpdate(releases, UpdateChannel.Stable, "0.7.0", Apk)?.Version);
        Assert.Equal("0.8.0-preview.2", GitHubReleases.PickUpdate(releases, UpdateChannel.Preview, "0.7.0", Apk)?.Version);
    }

    [Fact]
    public void Nothing_when_up_to_date_or_only_drafts_and_older_versions()
    {
        var releases = new[] { Release("v0.8.0", draft: true), Release("v0.7.0"), Release("v0.6.0"), Release("not-a-version") };

        Assert.Null(GitHubReleases.PickUpdate(releases, UpdateChannel.Stable, "0.7.0", Apk));
        // Android cannot downgrade: a preview newer than the next stable stays installed.
        Assert.Null(GitHubReleases.PickUpdate([Release("v0.8.0")], UpdateChannel.Stable, "0.8.1-preview.1", Apk));
    }

    [Fact]
    public void Asset_name_match_ignores_case()
    {
        var pick = GitHubReleases.PickUpdate([Release("v1.0.0", assets: "helm-ANDROID.apk")], UpdateChannel.Stable, "0.7.0", Apk);
        Assert.Equal("1.0.0", pick?.Version);
    }

    [Fact]
    public void Reads_the_github_api_shape_including_the_sha256_digest()
    {
        const string json = """
            [{"tag_name":"v0.8.0","draft":false,"prerelease":false,"body":"## Added","assets":[
              {"name":"Helm-android.apk","size":42,"browser_download_url":"https://github.com/x/Helm-android.apk",
               "digest":"sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789","content_type":"x"}]}]
            """;
        var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json)!;

        var pick = GitHubReleases.PickUpdate(releases, UpdateChannel.Stable, "0.7.0", Apk)!;

        Assert.Equal(42, pick.Asset.Size);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", pick.Sha256);
        Assert.Null(new ReleaseCandidate("1.0.0", null, new GitHubAsset(Apk, 1, "u", null)).Sha256);
    }

    [Fact]
    public void Secret_stores_round_trip_through_any_protector_and_refuse_other_purposes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-protector-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new PlainSecretProtector();
            var keys = new ProtectedMasterKeyStore(Path.Combine(dir, "master.key"), protector);
            keys.Save([1, 2, 3]);
            Assert.Equal([1, 2, 3], keys.Load());

            // A file protected for one purpose is not accepted as another secret.
            File.Copy(Path.Combine(dir, "master.key"), Path.Combine(dir, "local.key"));
            var local = new ProtectedLocalKeyStore(Path.Combine(dir, "local.key"), protector).GetOrCreate();
            Assert.Equal(SyncKeyring.KeySize, local.Length);
            Assert.Equal(local, new ProtectedLocalKeyStore(Path.Combine(dir, "local.key"), protector).GetOrCreate());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
