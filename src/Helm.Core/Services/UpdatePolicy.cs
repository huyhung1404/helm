using Helm.Core.Settings;

namespace Helm.Core.Services;

/// <summary>Pure rules for versions, channels and installation detection (unit-tested; no Velopack types).</summary>
public static class UpdatePolicy
{
    public const string StableChannel = "stable";
    public const string PreviewChannel = "preview";

    /// <summary>"v1.2.3" → "1.2.3"; "v0.4.0-preview.1" → "0.4.0-preview.1". Returns null for anything that is not a version tag.</summary>
    public static string? VersionFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        if (t.StartsWith("refs/tags/", StringComparison.Ordinal)) t = t["refs/tags/".Length..];
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        return TryParse(t, out _) ? t : null;
    }

    /// <summary>Tags with a pre-release suffix go to the preview channel; plain x.y.z tags are stable.</summary>
    public static string ChannelForVersion(string version) =>
        TryParse(version, out var v) && v.Prerelease.Length > 0 ? PreviewChannel : StableChannel;

    public static string ChannelName(UpdateChannel channel) => channel == UpdateChannel.Preview ? PreviewChannel : StableChannel;

    /// <summary>GitHub pre-releases are only considered on the preview channel.</summary>
    public static bool IncludePrereleases(UpdateChannel channel) => channel == UpdateChannel.Preview;

    /// <summary>Switching away from the channel the installed build came from may need a "downgrade" (preview → stable).</summary>
    public static bool AllowDowngrade(UpdateChannel selected, string? installedChannel) =>
        !string.Equals(ChannelName(selected), installedChannel ?? StableChannel, StringComparison.OrdinalIgnoreCase);

    /// <summary>SemVer 2.0 precedence: 1.0.0-preview.1 &lt; 1.0.0-preview.2 &lt; 1.0.0 &lt; 1.0.1. Build metadata is ignored.</summary>
    public static int Compare(string a, string b)
    {
        if (!TryParse(a, out var x)) throw new FormatException($"'{a}' is not a semantic version.");
        if (!TryParse(b, out var y)) throw new FormatException($"'{b}' is not a semantic version.");
        var c = x.Major.CompareTo(y.Major);
        if (c == 0) c = x.Minor.CompareTo(y.Minor);
        if (c == 0) c = x.Patch.CompareTo(y.Patch);
        if (c != 0) return c;

        if (x.Prerelease.Length == 0 || y.Prerelease.Length == 0)
            return y.Prerelease.Length.CompareTo(x.Prerelease.Length) switch { > 0 => 1, < 0 => -1, _ => 0 };

        for (var i = 0; i < Math.Min(x.Prerelease.Length, y.Prerelease.Length); i++)
        {
            var pa = x.Prerelease[i];
            var pb = y.Prerelease[i];
            var na = int.TryParse(pa, out var ia);
            var nb = int.TryParse(pb, out var ib);
            c = (na, nb) switch
            {
                (true, true) => ia.CompareTo(ib),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(pa, pb),
            };
            if (c != 0) return Math.Sign(c);
        }
        return x.Prerelease.Length.CompareTo(y.Prerelease.Length);
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    /// <summary>
    /// Why updates are unavailable, or null when this is a managed installation. Running from bin/ or a copied folder
    /// has no Update.exe next to the app root; portable packages manage themselves.
    /// </summary>
    public static string? NotInstalledReason(bool velopackReportsInstalled, bool isPortable, string? updateExePath, Func<string, bool> fileExists)
    {
        if (isPortable) return "This is a portable copy of Helm. Download new versions from the Releases page.";
        if (!velopackReportsInstalled || string.IsNullOrEmpty(updateExePath) || !fileExists(updateExePath))
            return "Helm is running from a development build or an unmanaged folder, so automatic updates are off. Install Helm with Helm-win-Setup.exe to get updates.";
        return null;
    }

    private readonly record struct SemVer(int Major, int Minor, int Patch, string[] Prerelease);

    private static bool TryParse(string text, out SemVer version)
    {
        version = default;
        var core = text.Split('+')[0];
        var dash = core.IndexOf('-');
        var numbers = (dash >= 0 ? core[..dash] : core).Split('.');
        if (numbers.Length != 3) return false;
        if (!int.TryParse(numbers[0], out var major) || !int.TryParse(numbers[1], out var minor) || !int.TryParse(numbers[2], out var patch)) return false;
        var pre = dash >= 0 ? core[(dash + 1)..].Split('.') : [];
        if (pre.Any(string.IsNullOrEmpty)) return false;
        version = new SemVer(major, minor, patch, pre);
        return true;
    }
}
