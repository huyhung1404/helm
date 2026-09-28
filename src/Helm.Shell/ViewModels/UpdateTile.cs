using Helm.Core.Services;

namespace Helm.Shell.ViewModels;

/// <summary>What the Home "Updates" tile shows; <see cref="Tone"/> is "ok", "attention" or "muted".</summary>
public sealed record UpdateTile(string Title, string Subtitle, string Tone, bool IsChecking)
{
    public static UpdateTile From(IUpdateService updates)
    {
        var version = updates.LastResult?.Version;
        var lastChecked = updates.LastChecked is { } checkedAt ? $"Last checked: {FormatWhen(checkedAt)}" : "Not checked yet";
        var (title, subtitle) = updates.State switch
        {
            UpdateState.Checking => ("Checking…", lastChecked),
            UpdateState.UpToDate => ("You're up to date", lastChecked),
            UpdateState.UpdateAvailable => ($"Update available · v{version}", "Open General → Updates"),
            UpdateState.Downloading => ($"Update available · v{version}", $"Downloading… {updates.DownloadProgress}%"),
            UpdateState.Downloaded => ($"Update available · v{version}", $"Ready — {updates.ApplyActionText.ToLowerInvariant()}"),
            UpdateState.Applying => ($"Updating to v{version}", "Restarting…"),
            UpdateState.NotInstalled => (updates.NotInstalledTitle, "Install Helm to get updates"),
            UpdateState.Failed => ("Couldn't check for updates", lastChecked),
            _ => ("You're up to date", lastChecked),
        };
        var tone = updates.State switch
        {
            UpdateState.UpdateAvailable or UpdateState.Downloading or UpdateState.Downloaded or UpdateState.Applying => "attention",
            UpdateState.NotInstalled or UpdateState.Failed => "muted",
            _ => "ok",
        };
        return new UpdateTile(title, subtitle, tone, updates.State == UpdateState.Checking);
    }

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var day = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("d");
        return $"{day} at {local:t}";
    }
}
