// Port of sea-g2p src/core/abbrev.rs.
namespace Helm.Speech.Vietnamese.Core;

/// <summary>How an abbreviation is read aloud. The reading mode is data, so no heuristic has to guess it.</summary>
internal enum ReadingKind
{
    /// <summary>Replace with words: "UBND" becomes "uỷ ban nhân dân".</summary>
    Expand,
    /// <summary>A fixed pronunciation kept verbatim, which may carry language markers.</summary>
    Fixed,
    /// <summary>Read as one English word ("NASA").</summary>
    WordEn,
    /// <summary>Spelled with the Vietnamese letter names.</summary>
    LettersNative,
    /// <summary>Spelled with the English letter names.</summary>
    LettersEn,
}

/// <summary>One table entry: the reading mode and, for Expand and Fixed, the replacement text.</summary>
internal readonly record struct Reading(ReadingKind Kind, string? Text = null);

/// <summary>
/// An abbreviation table matched on word boundaries. The first insert of a key wins, which is how the Vietnamese table
/// lets hand-written expansions outrank the mode-only groups.
/// </summary>
internal sealed class AbbrevTable
{
    private readonly Dictionary<string, Reading> _map = new(StringComparer.Ordinal);

    public void Insert(string key, Reading reading) => _map[key] = reading;

    public bool Contains(string key) => _map.ContainsKey(key);

    public Reading? Get(string key) => _map.TryGetValue(key, out var r) ? r : null;

    /// <summary>Keys whose reading replaces the token with plain text (Expand and Fixed).</summary>
    public IEnumerable<string> ReplacementKeys() =>
        _map.Where(kv => kv.Value.Kind is ReadingKind.Expand or ReadingKind.Fixed).Select(kv => kv.Key);

    /// <summary>The replacement text for an Expand or Fixed key, null for the other modes.</summary>
    public string? Replacement(string key) =>
        _map.TryGetValue(key, out var r) && r.Kind is ReadingKind.Expand or ReadingKind.Fixed ? r.Text : null;
}
