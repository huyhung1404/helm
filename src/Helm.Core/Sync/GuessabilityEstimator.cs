namespace Helm.Core.Sync;

/// <summary>
/// How many bits a password-cracking rule set (word lists, leetspeak, years, keyboard walks, repeats) would need to
/// find a passphrase: a small zxcvbn-style estimate. "Password@2026!!" scores low here although its characters span
/// every class, because crackers try "common word + year + symbols" first. Passphrases of ordinary unrelated words
/// still score well: only words that people put in passwords are in the list.
/// </summary>
internal static class GuessabilityEstimator
{
    // Base words of real-world password leaks, plus names and words common in Vietnamese passwords and a few that
    // belong to this app. Only matched as whole runs of letters of 3+ characters.
    private static readonly string[] Words =
    [
        "password", "passwd", "pass", "admin", "administrator", "root", "user", "login", "welcome", "letmein", "qwerty",
        "azerty", "iloveyou", "love", "lover", "secret", "master", "dragon", "monkey", "football", "soccer", "baseball",
        "shadow", "sunshine", "princess", "superman", "batman", "starwars", "hello", "freedom", "whatever", "trustno",
        "hunter", "killer", "michael", "jordan", "google", "facebook", "gmail", "yahoo", "apple", "samsung", "iphone",
        "android", "windows", "microsoft", "summer", "winter", "spring", "autumn", "january", "february", "march",
        "april", "june", "july", "august", "september", "october", "november", "december", "monday", "friday",
        "sunday", "abc", "abcd", "test", "demo", "guest", "default", "changeme", "computer", "internet", "server",
        "helm", "vault", "sync", "backup", "unity", "game", "ikame",
        "matkhau", "anhyeuem", "yeuem", "emyeuanh", "hanoi", "saigon", "vietnam", "viet", "nguyen", "tran", "pham",
        "hoang", "huynh", "phan", "dang", "bui", "ngo", "duong", "hung", "huy", "anh", "minh", "linh", "lan", "mai",
        "hoa", "thanh", "tuan", "duc", "dung", "long", "nam", "hai", "son", "trang", "thao", "ngoc", "quang", "khanh",
        "phuong", "thu", "ha", "hieu", "trung", "vinh", "binh", "cuong", "dat", "tien", "hoai", "quynh",
    ];

    private static readonly string[] KeyboardRows = ["qwertyuiop", "asdfghjkl", "zxcvbnm", "1234567890", "qazwsxedc"];

    private static readonly Dictionary<char, char> Leet = new()
    {
        ['@'] = 'a', ['4'] = 'a', ['0'] = 'o', ['1'] = 'i', ['!'] = 'i', ['3'] = 'e', ['5'] = 's', ['$'] = 's', ['7'] = 't', ['8'] = 'b',
    };

    public static double Bits(string passphrase)
    {
        // A block repeated several times is worth one block plus the repeat count.
        var period = RepeatPeriod(passphrase);
        if (period > 0) return Bits(passphrase[..period]) + Math.Log2(passphrase.Length / (double)period);

        var pool = Pool(passphrase);
        var charBits = Math.Log2(pool);
        var lower = passphrase.ToLowerInvariant();
        var leet = new string(lower.Select(c => Leet.TryGetValue(c, out var l) ? l : c).ToArray());
        double bits = 0;
        var i = 0;
        while (i < lower.Length)
        {
            if (MatchWord(lower, leet, i) is { } word)
            {
                // Rank in a large list (~2^11), plus a bit if its case or spelling was changed.
                var changed = passphrase.Substring(i, word) != lower.Substring(i, word) || lower.Substring(i, word) != leet.Substring(i, word);
                bits += 11 + (changed ? 1 : 0);
                i += word;
            }
            else if (IsYear(lower, i))
            {
                bits += 7;
                i += 4;
            }
            else if (RunLength(lower, i) is var run and >= 3)
            {
                bits += 4 + Math.Log2(run);
                i += run;
            }
            else if (KeyboardWalk(lower, i) is var walk and >= 4)
            {
                bits += 5 + Math.Log2(walk);
                i += walk;
            }
            else
            {
                bits += charBits;
                i++;
            }
        }
        return bits;
    }

    private static int? MatchWord(string lower, string leet, int start)
    {
        var best = 0;
        foreach (var word in Words)
        {
            if (word.Length <= best || start + word.Length > lower.Length) continue;
            if (string.CompareOrdinal(leet, start, word, 0, word.Length) == 0 || string.CompareOrdinal(lower, start, word, 0, word.Length) == 0)
                best = word.Length;
        }
        return best >= 3 ? best : null;
    }

    private static bool IsYear(string s, int i) =>
        i + 4 <= s.Length && s.Substring(i, 4).All(char.IsDigit) && s[i] is '1' or '2' && (s.Substring(i, 2) is "19" or "20");

    /// <summary>Length of a run of repeated characters or of steps of one (aaa, abc, 321).</summary>
    private static int RunLength(string s, int i)
    {
        if (i + 1 >= s.Length) return 1;
        var step = s[i + 1] - s[i];
        if (step is not (0 or 1 or -1)) return 1;
        var length = 2;
        while (i + length < s.Length && s[i + length] - s[i + length - 1] == step) length++;
        return length;
    }

    /// <summary>Length of a walk along a keyboard row, either direction (qwer, lkjh, 7890).</summary>
    private static int KeyboardWalk(string s, int i)
    {
        var best = 0;
        foreach (var row in KeyboardRows)
        {
            foreach (var line in new[] { row, new string(row.Reverse().ToArray()) })
            {
                var at = line.IndexOf(s[i]);
                if (at < 0) continue;
                var length = 1;
                while (i + length < s.Length && at + length < line.Length && s[i + length] == line[at + length]) length++;
                best = Math.Max(best, length);
            }
        }
        return best;
    }

    private static int RepeatPeriod(string s)
    {
        for (var period = 1; period <= s.Length / 2; period++)
        {
            if (s.Length % period != 0) continue;
            var repeated = true;
            for (var i = period; i < s.Length && repeated; i++) repeated = s[i] == s[i - period];
            if (repeated) return period;
        }
        return 0;
    }

    private static int Pool(string s)
    {
        var pool = 0;
        if (s.Any(char.IsLower)) pool += 26;
        if (s.Any(char.IsUpper)) pool += 26;
        if (s.Any(char.IsDigit)) pool += 10;
        if (s.Any(c => !char.IsLetterOrDigit(c))) pool += 33;
        return pool == 0 ? 26 : pool;
    }
}
