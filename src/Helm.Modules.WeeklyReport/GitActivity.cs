using System.Globalization;
using System.IO;
using System.Text;
using Helm.Core.Processes;

namespace Helm.Modules.WeeklyReport;

/// <summary>One commit of the user's.</summary>
public sealed record GitCommit(string Hash, DateTimeOffset When, string Subject, int Files, int Added, int Removed);

/// <summary>A repository with the user's commits of the week.</summary>
public sealed record GitProject(string Name, string Path, IReadOnlyList<GitCommit> Commits);

/// <summary>Runs git in a folder (a fake in tests).</summary>
public interface IGitRunner
{
    /// <summary>git's standard output, or null when it failed (not a repository, git missing…).</summary>
    Task<string?> RunAsync(string folder, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// The real git, hidden, with the user's normal rights even when Helm runs as administrator (git refuses repositories
/// owned by someone else: "dubious ownership").
/// </summary>
public sealed class GitRunner(IChildProcessLauncher launcher) : IGitRunner
{
    public async Task<string?> RunAsync(string folder, IReadOnlyList<string> args, CancellationToken ct)
    {
        ChildProcess process;
        try
        {
            process = launcher.Start(new ChildProcessStartInfo(CommandLine.Join(["git", "-c", "core.quotepath=false", .. args]), folder));
        }
        catch (ChildProcessException)
        {
            return null;
        }
        using (process)
        {
            process.StandardInput.Close();
            using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8);
            var errors = new StreamReader(process.StandardError).ReadToEndAsync(ct);
            string output;
            using (ct.Register(() =>
                   {
                       try
                       {
                           process.Kill();
                       }
                       catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                       {
                       }
                   }))
            {
                output = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
            }
            return await process.Exited.ConfigureAwait(false) == 0 ? output : null;
        }
    }
}

/// <summary>
/// What the user committed in a week, in the repositories under the folders they chose: every branch (also those
/// fetched from elsewhere), merges left out, a worktree counted once with its repository.
/// </summary>
public sealed class GitActivity(IGitRunner git)
{
    /// <summary>How deep under a chosen folder repositories are looked for.</summary>
    public const int Depth = 3;

    private const char Record = '\u001e';
    private const char Field = '\u001f';

    /// <summary>The user's name and e-mail in git's own settings (the default authors).</summary>
    public async Task<IReadOnlyList<string>> DefaultAuthorsAsync(CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>();
        foreach (var key in new[] { "user.email", "user.name" })
            if ((await git.RunAsync(home, ["config", "--global", key], ct).ConfigureAwait(false))?.Trim() is { Length: > 0 } value) list.Add(value);
        return list;
    }

    /// <summary>The repositories in <paramref name="roots"/> and below (a folder with .git, folder or file).</summary>
    public static IReadOnlyList<string> FindRepos(IEnumerable<string> roots, int depth = Depth)
    {
        var found = new List<string>();
        foreach (var root in roots.Select(r => r.Trim()).Where(r => r.Length > 0 && Directory.Exists(r)))
            Walk(root, depth);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Walk(string folder, int left)
        {
            if (Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git")))
            {
                found.Add(folder);
                return;
            }
            if (left == 0) return;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or "Library" or "Temp") continue;
                Walk(child, left - 1);
            }
        }
    }

    /// <summary>The user's commits from <paramref name="from"/> to <paramref name="to"/>, per repository (those with any).</summary>
    public async Task<IReadOnlyList<GitProject>> ReadAsync(IEnumerable<string> roots, IReadOnlyList<string> authors, DateTimeOffset from,
        DateTimeOffset to, IProgress<string>? progress, CancellationToken ct)
    {
        var projects = new List<GitProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repos = FindRepos(roots);
        var done = 0;
        foreach (var repo in repos)
        {
            progress?.Report($"Reading git: {++done} of {repos.Count} repositories…");
            // A worktree shares its repository's history: read each history once.
            var common = (await git.RunAsync(repo, ["rev-parse", "--git-common-dir"], ct).ConfigureAwait(false))?.Trim();
            if (common is null) continue;
            if (!seen.Add(Path.GetFullPath(Path.Combine(repo, common)))) continue;
            var log = await git.RunAsync(repo,
                ["log", "--all", "--no-merges", "--since=" + from.ToString("o", CultureInfo.InvariantCulture), "--until=" + to.ToString("o", CultureInfo.InvariantCulture),
                 $"--pretty=format:{Record}%H{Field}%aI{Field}%ae{Field}%an{Field}%s", "--shortstat"], ct).ConfigureAwait(false);
            if (log is null) continue;
            var commits = ParseLog(log, authors);
            if (commits.Count == 0) continue;
            var url = (await git.RunAsync(repo, ["config", "--get", "remote.origin.url"], ct).ConfigureAwait(false))?.Trim();
            projects.Add(new GitProject(ProjectName(repo, url), repo, commits));
        }
        return projects;
    }

    /// <summary>The repository's name: from its remote (…/game-x.git → game-x), else its folder.</summary>
    internal static string ProjectName(string folder, string? remoteUrl)
    {
        if (remoteUrl is { Length: > 0 })
        {
            var name = remoteUrl.TrimEnd('/').Split('/', ':').Last();
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length > 0) return name;
        }
        return Path.GetFileName(folder.TrimEnd('\\', '/'));
    }

    /// <summary>git log's records → the commits whose author's e-mail or name contains one of <paramref name="authors"/>.</summary>
    internal static IReadOnlyList<GitCommit> ParseLog(string log, IReadOnlyList<string> authors)
    {
        var list = new List<GitCommit>();
        foreach (var record in log.Split(Record, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) continue;
            var f = lines[0].Split(Field);
            if (f.Length < 5 || !DateTimeOffset.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
            if (authors.Count > 0 && !authors.Any(a => f[2].Contains(a, StringComparison.OrdinalIgnoreCase) || f[3].Contains(a, StringComparison.OrdinalIgnoreCase)))
                continue;
            var (files, added, removed) = lines.Length > 1 ? Stat(lines[^1]) : (0, 0, 0);
            list.Add(new GitCommit(f[0], when, f[4], files, added, removed));
        }
        return list.OrderBy(c => c.When).ToList();
    }

    /// <summary>" 3 files changed, 40 insertions(+), 2 deletions(-)" → (3, 40, 2).</summary>
    private static (int Files, int Added, int Removed) Stat(string line)
    {
        int files = 0, added = 0, removed = 0;
        foreach (var part in line.Split(',', StringSplitOptions.TrimEntries))
        {
            var number = int.TryParse(part.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (part.Contains("file", StringComparison.Ordinal)) files = number;
            else if (part.Contains("insertion", StringComparison.Ordinal)) added = number;
            else if (part.Contains("deletion", StringComparison.Ordinal)) removed = number;
        }
        return (files, added, removed);
    }
}
