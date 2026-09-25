using System.Reflection;

namespace Helm.App.Services;

internal static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/huyhung1404/helm";
    public const string IssuesUrl = RepositoryUrl + "/issues/new";
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    /// <summary>Semantic version from &lt;Version&gt; in Directory.Build.props (build metadata stripped).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
