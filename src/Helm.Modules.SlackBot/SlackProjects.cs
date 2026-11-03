using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Helm.Core.Processes;

namespace Helm.Modules.SlackBot;

/// <summary>The user's project folders, for Lumi: the ones a call names, and what happened in them lately (a fake in tests).</summary>
public interface ISlackProjects
{
    /// <summary>
    /// The folders <paramref name="text"/> points at: full paths written in it (C:\Work\Pitch, "D:\My Game"; a file
    /// stands for its folder) and folders of <paramref name="root"/> whose name is a word of it. Only existing ones.
    /// </summary>
    IReadOnlyList<string> Mentioned(string text, string root);

    /// <summary>Recent commits and uncommitted changes of <paramref name="folder"/>, as text; null when it is not a git repository.</summary>
    Task<string?> ProgressAsync(string folder, CancellationToken ct);
}

/// <summary>
/// Helm reads git itself (log and status, nothing else) and hands the text to Claude, so Claude never needs a shell to
/// tell how a project is going.
/// </summary>
public sealed partial class SlackProjects(IChildProcessLauncher launcher) : ISlackProjects
{
    /// <summary>Characters of git output Claude reads.</summary>
    internal const int MaxProgress = 6000;

    public IReadOnlyList<string> Mentioned(string text, string root)
    {
        var found = new List<string>();
        foreach (var path in Paths(text))
        {
            var folder = Directory.Exists(path) ? path : File.Exists(path) ? Path.GetDirectoryName(path) : null;
            // A whole drive is too much to hand over.
            if (folder is not null && Path.GetPathRoot(folder) is { } drive && !Same(folder, drive)) found.Add(Path.TrimEndingDirectorySeparator(folder));
        }
        // A project's name inside a path written out is not a second call for it.
        var words = PathText().Replace(text, " ");
        if (Directory.Exists(root))
            found.AddRange(Directory.EnumerateDirectories(root)
                .Where(d => Path.GetFileName(d) is { Length: >= 3 } name && !name.StartsWith('.') && Names(words, name)));
        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The full paths written in <paramref name="text"/>: quoted ("…", `…`) or up to the next space.</summary>
    internal static IEnumerable<string> Paths(string text) =>
        PathText().Matches(text).Select(m => (m.Groups["q"].Success ? m.Groups["q"].Value : m.Value).Trim().TrimEnd('.', ',', ')', ']', ':', ';'))
            .Where(p => p.Length > 3);

    internal static bool Names(string text, string name) =>
        Regex.IsMatch(text, $@"(^|[^\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase);

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("""["`](?<q>[A-Za-z]:[\\/][^"`]*)["`]|(?<![\p{L}\p{N}_])[A-Za-z]:[\\/][^\s"'`<>|*?]*""")]
    private static partial Regex PathText();

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
