namespace Helm.Connector;

/// <summary>
/// Where helm keeps Helm's data (settings, the sync replica, the agents' sign-ins, the host key): <c>--data-dir</c>,
/// else <c>$HELM_DATA_DIR</c> (a service sets it, e.g. /srv/fairy/helm), else <c>$XDG_DATA_HOME/helm</c>, else
/// <c>~/.local/share/helm</c>. Never the Windows app's folder: its secrets are DPAPI's, which helm cannot read.
/// </summary>
internal static class DataDirectory
{
    public const string Variable = "HELM_DATA_DIR";

    public static string Resolve(string? option, Func<string, string?> environment)
    {
        if (option is { Length: > 0 }) return Path.GetFullPath(option);
        if (environment(Variable) is { Length: > 0 } fromEnvironment) return Path.GetFullPath(fromEnvironment);
        // XDG: a relative XDG_DATA_HOME is invalid and ignored.
        var data = environment("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(environment("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "helm");
    }
}
