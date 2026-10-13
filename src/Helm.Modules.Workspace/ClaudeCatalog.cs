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
    private volatile IReadOnlyList<ModelInfo> _models = [];

    /// <summary>Claude Code's model list (the same for every folder); empty until the first answer.</summary>
    public IReadOnlyList<ModelInfo> Models => _models;

    /// <summary>Auto mode is offered (the default model supports it): one more stop of Shift+Tab.</summary>
    public bool AutoModeAvailable { get; private set; }

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

    /// <summary>
    /// One run of Claude Code in <paramref name="folder"/>: its commands (empty when Claude Code is missing or did not
    /// answer). The model list of the same answer is kept in <see cref="Models"/>.
    /// </summary>
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
            var (skills, models, auto) = await AskAsync(process, ct).ConfigureAwait(false);
            if (models.Count > 0)
            {
                _models = models;
                AutoModeAvailable = auto;
            }
            return skills;
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

    private static async Task<(IReadOnlyList<SkillInfo> Skills, IReadOnlyList<ModelInfo> Models, bool Auto)> AskAsync(ChildProcess process, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var input = new StreamWriter(process.StandardInput, new UTF8Encoding(false)) { NewLine = "\n" };
        await input.WriteLineAsync("""{"type":"control_request","request_id":"helm-init","request":{"subtype":"initialize"}}""".AsMemory(), timeout.Token).ConfigureAwait(false);
        await input.FlushAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8);
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            if (ParseInitialize(line) is { } skills)
            {
                var (models, auto) = ParseModels(line);
                return (skills, models, auto);
            }
        }
        return ([], [], false);
    }

    /// <summary>The model list of an <c>initialize</c> answer (empty for any other line), and whether Auto mode is offered.</summary>
    internal static (IReadOnlyList<ModelInfo> Models, bool AutoMode) ParseModels(string line)
    {
        if (line.Length == 0 || line[0] != '{') return ([], false);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var response = doc.RootElement.TryGetProperty("response", out var r) ? r : default;
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("response", out var inner)) response = inner;
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return ([], false);
            var list = new List<ModelInfo>();
            var auto = false;
            foreach (var m in models.EnumerateArray())
            {
                if (Str(m, "value") is not { Length: > 0 } value) continue;
                var levels = m.TryGetProperty("supportedEffortLevels", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : m.TryGetProperty("supportsEffort", out var se) && se.ValueKind == JsonValueKind.True ? ["low", "medium", "high"] : [];
                list.Add(new ModelInfo
                {
                    Value = value,
                    DisplayName = Str(m, "displayName") ?? value,
                    Description = Str(m, "description") ?? "",
                    ResolvedModel = Str(m, "resolvedModel") ?? value,
                    EffortLevels = levels,
                });
                if (value == "default" && m.TryGetProperty("supportsAutoMode", out var a) && a.ValueKind == JsonValueKind.True) auto = true;
            }
            return (list, auto);
        }
        catch (JsonException)
        {
            return ([], false);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
