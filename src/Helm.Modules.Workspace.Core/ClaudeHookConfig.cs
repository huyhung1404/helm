using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Modules.Workspace;

/// <summary>
/// Adds Helm's hooks to a Claude Code settings file (<c>~/.claude/settings.json</c>) and takes them out again,
/// leaving everything else as it was: other hooks (another notifier, your own scripts), permissions, plugins. Helm's
/// entries are <c>http</c> hooks whose URL path is <see cref="UrlPath"/>; nothing else is ever changed or removed.
/// </summary>
public static class ClaudeHookConfig
{
    /// <summary>The path that marks a hook as Helm's.</summary>
    public const string UrlPath = "/helm/workspace/hook";

    /// <summary>Seconds Claude waits for Helm per hook. Helm answers at once; a closed Helm refuses at once.</summary>
    public const int TimeoutSeconds = 3;

    /// <summary>
    /// The PermissionRequest hook may wait this long: Helm holds it while the phone decides (always less than this,
    /// see <see cref="WorkspaceSettings.HoldMinutes"/>), and answers at once otherwise.
    /// </summary>
    public const int PermissionTimeoutSeconds = 1800;

    public static int TimeoutFor(string hookEvent) => hookEvent == ClaudeHookEvent.PermissionRequest ? PermissionTimeoutSeconds : TimeoutSeconds;

    public static string HookUrl(int port) => $"http://127.0.0.1:{port}{UrlPath}";

    /// <summary>Claude Code's user settings file.</summary>
    public static string UserSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>Settings JSON with Helm's hooks for <paramref name="port"/> (old Helm entries replaced).</summary>
    /// <exception cref="JsonException">The file is not a JSON object; the caller leaves it alone.</exception>
    public static string Install(string? json, int port, string token)
    {
        var root = ParseRoot(json);
        RemoveHelmHooks(root);
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var name in ClaudeHookEvent.Subscribed)
        {
            if (hooks[name] is not JsonArray list)
            {
                list = new JsonArray();
                hooks[name] = list;
            }
            var entry = new JsonObject();
            // Only the question tool before it runs; every tool after it ran (that is how "back at work" is seen).
            if (name == ClaudeHookEvent.PreToolUse) entry["matcher"] = "AskUserQuestion";
            entry["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "http",
                ["url"] = HookUrl(port),
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
                ["timeout"] = TimeoutFor(name),
            });
            list.Add(entry);
        }
        return Write(root);
    }

    /// <summary>Settings JSON without Helm's hooks; null when there were none (nothing to write).</summary>
    public static string? Remove(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var root = ParseRoot(json);
        return RemoveHelmHooks(root) ? Write(root) : null;
    }

    /// <summary>True when the file has Helm's hooks for exactly this port and token (no rewrite needed).</summary>
    public static bool IsInstalled(string? json, int port, string token)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        JsonObject root;
        try
        {
            root = ParseRoot(json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (root["hooks"] is not JsonObject hooks) return false;
        foreach (var name in ClaudeHookEvent.Subscribed)
        {
            var ours = (hooks[name] as JsonArray)?.OfType<JsonObject>().SelectMany(HelmHooksIn).ToList() ?? [];
            if (ours.Count != 1) return false;
            var hook = ours[0];
            if (hook["url"]?.GetValue<string>() != HookUrl(port)) return false;
            if (hook["headers"]?["Authorization"]?.GetValue<string>() != "Bearer " + token) return false;
            if (hook["timeout"] is not JsonValue t || t.GetValueKind() != JsonValueKind.Number || t.GetValue<int>() != TimeoutFor(name)) return false;
        }
        return true;
    }

    private static JsonObject ParseRoot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node as JsonObject ?? throw new JsonException("Claude settings are not a JSON object.");
    }

    /// <summary>Takes out every hook that is Helm's; drops matcher entries and event lists that only held Helm's.</summary>
    private static bool RemoveHelmHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        var changed = false;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray list) continue;
            var listChanged = false;
            foreach (var entry in list.OfType<JsonObject>().ToList())
            {
                if (entry["hooks"] is not JsonArray inner) continue;
                var ours = inner.OfType<JsonObject>().Where(IsHelmHook).ToList();
                if (ours.Count == 0) continue;
                foreach (var hook in ours) inner.Remove(hook);
                if (inner.Count == 0) list.Remove(entry);
                listChanged = true;
            }
            if (listChanged && list.Count == 0) hooks.Remove(name);
            changed |= listChanged;
        }
        if (changed && hooks.Count == 0) root.Remove("hooks");
        return changed;
    }

    private static IEnumerable<JsonObject> HelmHooksIn(JsonObject entry) =>
        (entry["hooks"] as JsonArray)?.OfType<JsonObject>().Where(IsHelmHook) ?? [];

    private static bool IsHelmHook(JsonObject hook)
    {
        if (hook["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String || type.GetValue<string>() != "http") return false;
        if (hook["url"] is not JsonValue url || url.GetValueKind() != JsonValueKind.String) return false;
        return Uri.TryCreate(url.GetValue<string>(), UriKind.Absolute, out var uri) && uri.IsLoopback && uri.AbsolutePath == UrlPath;
    }

    private static string Write(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    /// <summary>
    /// Reads, changes and writes a settings file safely: a one-time backup (<c>settings.json.helm-backup</c>) before
    /// Helm first changes it, and an atomic replace so Claude never reads half a file. Returns false when nothing
    /// changed.
    /// </summary>
    public static bool Rewrite(string path, Func<string?, string?> change)
    {
        var before = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        var after = change(before);
        if (after is null || after == before) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".helm-backup";
        if (before is not null && !File.Exists(backup)) File.WriteAllText(backup, before, new UTF8Encoding(false));
        var temp = path + ".helm-tmp";
        File.WriteAllText(temp, after, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return true;
    }
}
