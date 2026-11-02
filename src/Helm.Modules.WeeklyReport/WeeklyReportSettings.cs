using System.IO;
using Helm.Core.Settings;

namespace Helm.Modules.WeeklyReport;

/// <summary>Weekly Report's options, on this PC only (they name the user's work folders and projects).</summary>
public sealed class WeeklyReportSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Folders with the user's repositories (looked into a few levels deep); empty = the usual places.</summary>
    public List<string> RepoFolders { get; set; } = [];

    /// <summary>The user's git e-mails and names; empty = git's own user.email and user.name.</summary>
    public List<string> Authors { get; set; } = [];

    /// <summary>Projects and conversations the user unticked: unticked again next week.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Notes for every report (the manager's taste, the user's role), in the user's own words.</summary>
    public string Notes { get; set; } = "";

    /// <summary>Claude Code model ("" = the user's default).</summary>
    public string Model { get; set; } = "";

    /// <summary>The folders used when the user chose none: C:\Project and Visual Studio's source\repos, where they exist.</summary>
    public static IReadOnlyList<string> UsualFolders() =>
        new[] { @"C:\Project", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos") }
            .Where(Directory.Exists)
            .ToList();
}
