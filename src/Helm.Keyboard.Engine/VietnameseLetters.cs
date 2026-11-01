namespace Helm.Typing;

/// <summary>What a Telex modifier key adds to a letter: â ê ô (aa ee oo), ă (aw), ơ ư (ow uw), đ (dd).</summary>
internal enum Mark : byte
{
    None,
    Circumflex,
    Breve,
    Horn,
    Stroke,
}

/// <summary>The six tones, in the order of the letter tables below.</summary>
internal enum Tone : byte
{
    None,
    Grave,  // huyền (f)
    Acute,  // sắc (s)
    Hook,   // hỏi (r)
    Tilde,  // ngã (x)
    Dot,    // nặng (j)
}

/// <summary>
/// Vietnamese letters built from (letter, mark, tone) and taken apart again, from fixed tables: no Unicode
/// normalization, so it behaves the same in every app the input method is loaded into.
/// </summary>
internal static class VietnameseLetters
{
    // One row per vowel; columns follow Tone: none, huyền, sắc, hỏi, ngã, nặng.
    private const string Vowels = "aăâeêioôơuưy";
    private static readonly string[] Lower =
        ["aàáảãạ", "ăằắẳẵặ", "âầấẩẫậ", "eèéẻẽẹ", "êềếểễệ", "iìíỉĩị", "oòóỏõọ", "ôồốổỗộ", "ơờớởỡợ", "uùúủũụ", "ưừứửữự", "yỳýỷỹỵ"];
    private static readonly string[] Upper =
        ["AÀÁẢÃẠ", "ĂẰẮẲẴẶ", "ÂẦẤẨẪẬ", "EÈÉẺẼẸ", "ÊỀẾỂỄỆ", "IÌÍỈĨỊ", "OÒÓỎÕỌ", "ÔỒỐỔỖỘ", "ƠỜỚỞỠỢ", "UÙÚỦŨỤ", "ƯỪỨỬỮỰ", "YỲÝỶỸỴ"];

    public static bool IsVowel(char letter) => letter is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';

    /// <summary>The letter written with its mark and tone ('e', Circumflex, Dot → 'ệ').</summary>
    public static char Compose(char letter, Mark mark, Tone tone, bool upper)
    {
        var marked = WithMark(letter, mark);
        if (marked == 'đ') return upper ? 'Đ' : 'đ';
        var row = Vowels.IndexOf(marked);
        if (row < 0) return upper ? char.ToUpperInvariant(letter) : letter;
        return (upper ? Upper : Lower)[row][(int)tone];
    }

    /// <summary>The inverse of <see cref="Compose"/> for a Vietnamese or ASCII letter; false for anything else.</summary>
    public static bool TryDecompose(char c, out char letter, out Mark mark, out Tone tone, out bool upper)
    {
        mark = Mark.None;
        tone = Tone.None;
        upper = false;
        letter = c;
        if (c is 'đ' or 'Đ')
        {
            (letter, mark, upper) = ('d', Mark.Stroke, c == 'Đ');
            return true;
        }
        for (var row = 0; row < Vowels.Length; row++)
        {
            var at = Lower[row].IndexOf(c);
            if (at < 0 && (at = Upper[row].IndexOf(c)) >= 0) upper = true;
            if (at < 0) continue;
            tone = (Tone)at;
            (letter, mark) = Unmark(Vowels[row]);
            return true;
        }
        if (c is >= 'a' and <= 'z') return true;
        if (c is >= 'A' and <= 'Z')
        {
            (letter, upper) = (char.ToLowerInvariant(c), true);
            return true;
        }
        return false;
    }

    /// <summary>The lowercase letter with its mark but no tone ('o', Horn → 'ơ').</summary>
    public static char WithMark(char letter, Mark mark) => (letter, mark) switch
    {
        ('a', Mark.Circumflex) => 'â',
        ('a', Mark.Breve) => 'ă',
        ('e', Mark.Circumflex) => 'ê',
        ('o', Mark.Circumflex) => 'ô',
        ('o', Mark.Horn) => 'ơ',
        ('u', Mark.Horn) => 'ư',
        ('d', Mark.Stroke) => 'đ',
        _ => letter,
    };

    /// <summary>A lowercase letter without mark or tone ('ơ' → 'o').</summary>
    public static char Base(char marked) => Unmark(marked).Letter;

    private static (char Letter, Mark Mark) Unmark(char marked) => marked switch
    {
        'â' => ('a', Mark.Circumflex),
        'ă' => ('a', Mark.Breve),
        'ê' => ('e', Mark.Circumflex),
        'ô' => ('o', Mark.Circumflex),
        'ơ' => ('o', Mark.Horn),
        'ư' => ('u', Mark.Horn),
        'đ' => ('d', Mark.Stroke),
        _ => (marked, Mark.None),
    };
}
