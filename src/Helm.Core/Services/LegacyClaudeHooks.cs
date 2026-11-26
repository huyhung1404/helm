using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Services;

/// <summary>
/// Workspace Controller (removed 2026-11-26) added <c>http</c> hooks to Claude Code's user settings
/// (<c>~/.claude/settings.json</c>, URL path <see cref="UrlPath"/> on 127.0.0.1) and took them out when it was turned off.
/// It is gone, so nothing would take them out any more: Helm removes them once at start. Only those hooks are touched;
/// everything else in the file stays as it is, and the file is rewritten only when one was there.
/// </summary>
public static class LegacyClaudeHooks
{
    /// <summary>The path that marked a hook as Workspace Controller's.</summary>
    public const string UrlPath = "/helm/workspace/hook";

    public static string UserSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>Removes the old hooks from the user's settings file, in the background; problems are only logged.</summary>
    public static void RemoveInBackground(ILogger logger) => Task.Run(() =>
    {
        try
        {
            var path = UserSettingsPath();
            if (!File.Exists(path)) return;
            var before = File.ReadAllText(path, Encoding.UTF8);
            if (Remove(before) is not { } after) return;
            var temp = path + ".helm-tmp";
            File.WriteAllText(temp, after, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            logger.LogInformation("Removed Workspace Controller's old hooks from Claude Code's settings");
        }
        catch (Exception ex)
        {
            // A file Claude is writing, or not JSON: leave it alone.
            logger.LogWarning(ex, "Could not remove Workspace Controller's old hooks from Claude Code's settings");
        }
    });

    /// <summary>The settings JSON without the old hooks; null when there were none (nothing to write).</summary>
    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static string? Remove(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (node is not JsonObject root || root["hooks"] is not JsonObject hooks) return null;
        var changed = false;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray list) continue;
            var listChanged = false;
            foreach (var entry in list.OfType<JsonObject>().ToList())
            {
                if (entry["hooks"] is not JsonArray inner) continue;
                var old = inner.OfType<JsonObject>().Where(IsOld).ToList();
                if (old.Count == 0) continue;
                foreach (var hook in old) inner.Remove(hook);
                if (inner.Count == 0) list.Remove(entry);
                listChanged = true;
            }
            if (listChanged && list.Count == 0) hooks.Remove(name);
            changed |= listChanged;
        }
        if (!changed) return null;
        if (hooks.Count == 0) root.Remove("hooks");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static bool IsOld(JsonObject hook)
    {
        if (hook["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String || type.GetValue<string>() != "http") return false;
        if (hook["url"] is not JsonValue url || url.GetValueKind() != JsonValueKind.String) return false;
        return Uri.TryCreate(url.GetValue<string>(), UriKind.Absolute, out var uri) && uri.IsLoopback && uri.AbsolutePath == UrlPath;
    }
}
