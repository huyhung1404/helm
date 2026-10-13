using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// The slash commands and skills Claude Code offers in a folder (the user's, the project's, the built-in ones), for the
/// phone's "/" suggestions. Asked from Claude Code itself: a short <c>claude -p</c> in stream-json mode answers the
/// <c>initialize</c> request with its command list and is closed again. That costs no API usage, and the run has hooks
/// turned off so it never shows up as a session.
/// </summary>
public sealed class ClaudeCatalog(IChildProcessLauncher launcher, ILogger<ClaudeCatalog> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A folder's list is asked again after this long (new skills appear without restarting Helm).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<SkillInfo> Skills)> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A folder's list arrived.</summary>
    public event EventHandler? Updated;

    /// <summary>Every command known for the connected folders, by name (a project's own skill once).</summary>
    public IReadOnlyList<SkillInfo> Current =>
        _byFolder.Values.SelectMany(v => v.Skills).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Asks Claude Code about each folder whose list is missing or old, in the background.</summary>
    public void Refresh(IEnumerable<string> folders)
    {
        foreach (var folder in folders.Where(f => f.Length > 0))
        {
            var key = SessionRegistry.FolderKey(folder);
            if (_byFolder.TryGetValue(key, out var known) && DateTimeOffset.Now - known.At < MaxAge) continue;
            if (!_loading.TryAdd(key, 0)) continue;
            _ = Task.Run(async () =>
            {
                try
                {
                    var skills = await LoadAsync(folder, CancellationToken.None).ConfigureAwait(false);
                    if (skills.Count == 0) return;
                    _byFolder[key] = (DateTimeOffset.Now, skills);
                    Updated?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Workspace: Claude Code's commands for {Folder} could not be read", folder);
                }
                finally
                {
                    _loading.TryRemove(key, out _);
                }
            });
        }
    }

    /// <summary>One run of Claude Code in <paramref name="folder"/>; empty when Claude Code is missing or did not answer.</summary>
    public async Task<IReadOnlyList<SkillInfo>> LoadAsync(string folder, CancellationToken ct)
    {
        if (McpClientConfig.FindClaude() is not { } claude || !Directory.Exists(folder)) return [];
        // One file per run: folders are asked at the same time.
        var settings = Path.Combine(Path.GetTempPath(), $"helm-claude-catalog-{Guid.NewGuid():N}.json");
        // No hooks for this run: it is Helm asking, not a session of yours.
        await File.WriteAllTextAsync(settings, """{"disableAllHooks":true}""", ct).ConfigureAwait(false);
        IReadOnlyList<string> args = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--settings", settings];
        var commandLine = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"{CommandLine.Quote(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([claude, .. args])}\""
            : CommandLine.Join([claude, .. args]);
        using var process = launcher.Start(new ChildProcessStartInfo(commandLine, folder));
        try
        {
            return await AskAsync(process, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            try
            {
                File.Delete(settings);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<IReadOnlyList<SkillInfo>> AskAsync(ChildProcess process, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var input = new StreamWriter(process.StandardInput, new UTF8Encoding(false)) { NewLine = "\n" };
        await input.WriteLineAsync("""{"type":"control_request","request_id":"helm-init","request":{"subtype":"initialize"}}""".AsMemory(), timeout.Token).ConfigureAwait(false);
        await input.FlushAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8);
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            if (ParseInitialize(line) is { } skills) return skills;
        }
        return [];
    }

    /// <summary>The command list of an <c>initialize</c> answer; null for any other line.</summary>
    internal static IReadOnlyList<SkillInfo>? ParseInitialize(string line)
    {
        if (line.Length == 0 || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "control_response") return null;
            if (!root.TryGetProperty("response", out var response)) return null;
            if (response.TryGetProperty("response", out var inner)) response = inner;
            if (!response.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) return [];
            var list = new List<SkillInfo>();
            foreach (var c in commands.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                var description = c.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                list.Add(new SkillInfo(name.TrimStart('/'), description.Length > 200 ? description[..199] + "…" : description));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
