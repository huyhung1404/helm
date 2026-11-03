using System.Text.Json;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;

namespace Helm.Ai.Auto;

/// <summary>
/// Helm's own tools for the automatic jobs, run in-process (no MCP, nobody to ask): reading tools freely, and only the
/// changes Helm can undo. A tool of a tool that is off is not there.
/// </summary>
public sealed class AutoJobTools(IEnumerable<IMcpToolProvider> providers, Func<string?, bool> isToolOn)
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IReadOnlyList<IMcpToolProvider> _providers = [.. providers];

    public async Task<JsonElement?> ReadAsync(string name, object? args, CancellationToken ct)
    {
        if (Find(name) is not { ReadOnly: true } tool) return null;
        try
        {
            return JsonSerializer.SerializeToElement(await tool.Run(JsonSerializer.SerializeToElement(args ?? new { }, Output), ct), Output);
        }
        catch (McpToolException)
        {
            return null;
        }
    }

    public async Task<AiJobAction?> ChangeAsync(string name, object args, string summary, CancellationToken ct)
    {
        if (Find(name) is not { ReadOnly: false, Undo: not null } tool) return null;
        try
        {
            var result = await tool.Run(JsonSerializer.SerializeToElement(args, Output), ct);
            return new AiJobAction(name, summary, JsonSerializer.Serialize(result, Output));
        }
        catch (McpToolException)
        {
            return null;
        }
    }

    private McpTool? Find(string name) =>
        _providers.Where(p => isToolOn(p.ModuleId)).SelectMany(p => p.Tools).FirstOrDefault(t => t.Name == name);
}
