using System.Security.Cryptography;

namespace Helm.Modules.Vault.Items;

public sealed record PasswordOptions(
    int Length = 20,
    bool Lowercase = true,
    bool Uppercase = true,
    bool Digits = true,
    bool Symbols = true,
    bool AvoidAmbiguous = true)
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
}

/// <summary>Random passwords from the OS CSPRNG, uniform over the chosen alphabet, with one character of each chosen class.</summary>
public static class PasswordGenerator
{
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitChars = "0123456789";
    private const string SymbolChars = "!#$%&*+-=?@^_~.:,;";
    private const string Ambiguous = "Il1O0o";

    public static string Generate(PasswordOptions options)
    {
        var length = Math.Clamp(options.Length, PasswordOptions.MinLength, PasswordOptions.MaxLength);
        var classes = Classes(options);
        var alphabet = string.Concat(classes);
        var chars = new char[length];
        // One of each class first, the rest from the whole alphabet, then an unbiased shuffle.
        for (var i = 0; i < classes.Count; i++) chars[i] = Pick(classes[i]);
        for (var i = classes.Count; i < length; i++) chars[i] = Pick(alphabet);
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    /// <summary>Bits of entropy of a generated password (log2 of the alphabet size, times the length).</summary>
    public static double EntropyBits(PasswordOptions options) =>
        Math.Clamp(options.Length, PasswordOptions.MinLength, PasswordOptions.MaxLength) * Math.Log2(string.Concat(Classes(options)).Length);

    private static List<string> Classes(PasswordOptions options)
    {
        var classes = new List<string>();
        if (options.Lowercase) classes.Add(Lower);
        if (options.Uppercase) classes.Add(Upper);
        if (options.Digits) classes.Add(DigitChars);
        if (options.Symbols) classes.Add(SymbolChars);
        if (classes.Count == 0) classes.Add(Lower);
        return options.AvoidAmbiguous ? classes.Select(c => new string(c.Where(ch => !Ambiguous.Contains(ch)).ToArray())).ToList() : classes;
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
