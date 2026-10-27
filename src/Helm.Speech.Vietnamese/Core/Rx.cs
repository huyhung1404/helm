// Port support for sea-g2p (Rust): regex construction and capture access with Rust-regex semantics.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Helm.Speech.Vietnamese.Core;

/// <summary>
/// Helpers shared by every ported pattern. The Rust <c>regex</c> and <c>fancy-regex</c> crates are leftmost-first
/// backtracking-equivalent engines with Unicode <c>\w \d \s \b</c>, which .NET's engine matches when the patterns are
/// written with <c>\z</c> for Rust's end anchor and compiled culture-invariant.
/// </summary>
internal static class Rx
{
    /// <summary>Options every ported pattern uses; the case-insensitive ones carry an inline <c>(?i)</c> like the original.</summary>
    public const RegexOptions Options = RegexOptions.CultureInvariant;

    // Word characters. Rust's \w is Alphabetic + every mark + Nd + Pc + the joiners; .NET's \w misses the spacing and
    // enclosing marks (Mc, Me), the letter numbers (Nl: Ⅻ) and the circled letters (ⓐ), which Unicode calls Alphabetic.
    // Characters outside the BMP (𝐀, CJK extension B) are still a difference: .NET sees two surrogates there.

    /// <summary>The BMP characters Rust counts as word characters and .NET does not.</summary>
    public const string WordGap = @"\p{Mc}\p{Me}\p{Nl}Ⓐ-ⓩ";

    /// <summary>The inside of a character class matching Rust's <c>\w</c> (on the BMP).</summary>
    public const string WordClass = @"\p{L}\p{M}\p{Nd}\p{Nl}\p{Pc}‌‍Ⓐ-ⓩ";

    /// <summary>Rust's <c>\w</c>.</summary>
    public const string W = "[" + WordClass + "]";

    /// <summary>Rust's <c>\b</c> where the character after it is known to be a word character ("\b\d", "\b[A-Z]").</summary>
    public const string BL = @"\b(?<![" + WordGap + "])";

    /// <summary>Rust's <c>\b</c> where the character before it is known to be a word character ("\d\b").</summary>
    public const string BR = @"\b(?![" + WordGap + "])";

    /// <summary>Rust's <c>\b</c> where either side may be a non-word character; slower, so used only off the hot start.</summary>
    public const string B = "(?:(?<=" + W + ")(?!" + W + ")|(?<!" + W + ")(?=" + W + "))";

    /// <summary>
    /// Build a pattern that is assembled at run time from a table (abbreviations, units). It is compiled to IL where the
    /// runtime can do so and interpreted otherwise (AOT phones).
    /// </summary>
    public static Regex Build(string pattern) =>
        new(pattern, Options | (RuntimeFeature.IsDynamicCodeCompiled ? RegexOptions.Compiled : RegexOptions.None));

    /// <summary>The text of a capture group, or null when it did not take part (Rust's <c>caps.get(i)</c> is None).</summary>
    public static string? G(this Match m, int group)
    {
        var g = m.Groups[group];
        return g.Success ? g.Value : null;
    }

    /// <summary>The text of a capture group that always takes part (Rust's <c>caps.get(i).unwrap()</c>).</summary>
    public static string V(this Match m, int group) => m.Groups[group].Value;

    /// <summary>Rust's <c>regex::escape</c>: .NET escapes a superset, which matches the same text.</summary>
    public static string Escape(string literal) => Regex.Escape(literal);
}
