using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>
/// How a photo reaches an agent CLI with the words of a turn: Claude Code takes image blocks in its stream-json user
/// message, Codex's app server image inputs in turn/start (a data URL: nothing on disk), and <c>codex exec</c> image
/// files (<c>--image</c>), written into the agent's empty work folder for that run only. Grok's CLI has no way in for
/// a picture (its only rich prompt, <c>--prompt-json</c>, goes on the command line), so a Grok agent takes no photo job.
/// </summary>
public static class CliImages
{
    /// <summary>
    /// Claude Code's user message content (<c>--input-format stream-json</c>): the words alone as a string, else a text
    /// block and one base64 image block per photo, as the Messages API has them.
    /// </summary>
    public static JsonNode ClaudeCodeContent(string text, IReadOnlyList<AssistantImage> images)
    {
        if (images.Count == 0) return JsonValue.Create(text);
        var content = new JsonArray();
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) },
            });
        }
        return content;
    }

    /// <summary>
    /// The input of a Codex app server turn (codex 0.166, <c>codex app-server generate-json-schema</c>: UserInput
    /// <c>{"type":"text"}</c> and <c>{"type":"image","url"}</c>): the words, then each photo as a data URL.
    /// </summary>
    public static JsonArray CodexInput(string text, IReadOnlyList<AssistantImage> images)
    {
        var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images) input.Add(new JsonObject { ["type"] = "image", ["url"] = DataUrl(image) });
        return input;
    }

    public static string DataUrl(AssistantImage image) => $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}";

    /// <summary>
    /// A <c>codex exec</c> run's arguments with the photo files attached to its prompt: <c>--image</c> takes several
    /// values, so a <c>--</c> ends them before the prompt read from stdin (the last argument, <c>-</c>).
    /// </summary>
    public static string[] WithCodexImages(IReadOnlyList<string> args, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return [.. args];
        if (args.Count == 0 || args[^1] != "-") throw new ArgumentException("The prompt must come last, from stdin.", nameof(args));
        return [.. args.Take(args.Count - 1), .. files.SelectMany(f => new[] { "--image", f }), "--", "-"];
    }

    /// <summary>Writes the photos into <paramref name="folder"/> under random names, for one run; <see cref="Delete"/> after it.</summary>
    public static IReadOnlyList<string> Save(string folder, IReadOnlyList<AssistantImage> images)
    {
        var files = new List<string>();
        try
        {
            foreach (var image in images)
            {
                var path = Path.Combine(folder, $"photo-{Guid.NewGuid():N}{Extension(image.MediaType)}");
                files.Add(path);
                File.WriteAllBytes(path, image.Data);
            }
            return files;
        }
        catch
        {
            Delete(files);
            throw;
        }
    }

    /// <summary>Deletes the photo files of a run (one that cannot be deleted now is left; never throws).</summary>
    public static void Delete(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static string Extension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg",
    };
}
