using System.Globalization;
using Helm.Core.Mcp;

namespace Helm.Modules.Scratch;

/// <summary>
/// What lies in Scratch, told without its contents: for Helm's own tidy-up suggestions on the PC (never on the MCP
/// server: Scratch holds what was copied, codes and links among them). A text gives only its first words, and one that
/// looks like a code or a key not even those.
/// </summary>
public sealed class ScratchDigestTool(ScratchStore store) : IMcpToolProvider
{
    private const int MaxItems = 60;
    private const int ShownLength = 60;

    public string? ModuleId => ScratchIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("scratch_digest", "The things in Scratch, newest first: kind, a short name, size, how old, and the days left before Scratch trashes it unused.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(Digest())) { ReadOnly = true },
    ];

    private object Digest()
    {
        var now = store.Now;
        return store.Items(ScratchFilter.All).Take(MaxItems).Select(i => new
        {
            id = i.Id,
            kind = i.Value.Kind == ScratchKind.Text ? "text" : i.Value.IsImage ? "photo" : i.Value.IsVideo ? "video" : "file",
            name = Shown(i.Value),
            size = i.Value.Kind == ScratchKind.Text ? $"{i.Value.Size} characters" : $"{Math.Max(1, i.Value.Size / 1024).ToString(CultureInfo.InvariantCulture)} KB",
            daysOld = (int)(now - i.Value.AddedAt).TotalDays,
            daysLeft = Math.Max(0, (int)Math.Ceiling((ScratchStore.IdleUntil(i.Value) - now).TotalDays)),
            canBecomeNote = i.Value.CanBecomeNote,
        }).ToList();
    }

    private static string Shown(ScratchItem item)
    {
        if (item.Kind != ScratchKind.Text) return Cut(item.Name);
        var first = ScratchFormat.FirstLine(item.Text ?? "", 200).Trim();
        return LooksSecret(first) ? "(a code or key, not shown)" : Cut(first);
    }

    /// <summary>One long word of letters and digits mixed (a password, a token, an OTP), not a link.</summary>
    internal static bool LooksSecret(string text)
    {
        if (text.Length < 6 || text.Contains(' ') || text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        return text.Any(char.IsDigit) && (text.Any(char.IsLetter) || text.Length <= 8) || text.Length >= 20;
    }

    private static string Cut(string text) => text.Length <= ShownLength ? text : text[..ShownLength] + "…";
}
