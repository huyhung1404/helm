using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Helm.Core.Processes;

namespace Helm.Modules.SlackBot;

/// <summary>The user's project folders, for Lumi: the one a call names, and what happened in it lately (a fake in tests).</summary>
public interface ISlackProjects
{
    /// <summary>The folder under <paramref name="root"/> whose name is a word of <paramref name="text"/> (the longest), or null.</summary>
    string? Named(string text, string root);

    /// <summary>Recent commits and uncommitted changes of <paramref name="folder"/>, as text; null when it is not a git repository.</summary>
    Task<string?> ProgressAsync(string folder, CancellationToken ct);
}

/// <summary>
/// Helm reads git itself (log and status, nothing else) and hands the text to Claude, so Claude never needs a shell to
/// tell how a project is going.
/// </summary>
public sealed class SlackProjects(IChildProcessLauncher launcher) : ISlackProjects
{
    /// <summary>Characters of git output Claude reads.</summary>
    internal const int MaxProgress = 6000;

    public string? Named(string text, string root)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Select(d => (Path: d, Name: Path.GetFileName(d)))
            .Where(d => d.Name.Length >= 3 && !d.Name.StartsWith('.') && Names(text, d.Name))
            .OrderByDescending(d => d.Name.Length)
            .Select(d => d.Path)
            .FirstOrDefault();
    }

    internal static bool Names(string text, string name) =>
        Regex.IsMatch(text, $@"(^|[^\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase);

    public async Task<string?> ProgressAsync(string folder, CancellationToken ct)
    {
        var log = await GitAsync(folder, ["log", "--all", "--no-merges", "--since=21.days", "-n", "60", "--date=short",
            "--pretty=format:%ad %an: %s"], ct).ConfigureAwait(false);
        if (log is null) return null;
        var status = await GitAsync(folder, ["status", "--short", "--branch"], ct).ConfigureAwait(false);
        var text = $"Commits of the last 3 weeks (newest first):\n{(log.Trim().Length > 0 ? log.Trim() : "(none)")}\n\n" +
                   $"Not committed yet:\n{status?.Trim()}";
        return SlackText.Clip(text, MaxProgress);
    }

    private async Task<string?> GitAsync(string folder, IReadOnlyList<string> args, CancellationToken ct)
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
