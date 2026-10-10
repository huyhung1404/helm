using System.Text;
using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Dictionaries;

/// <summary>
/// One QuickTranslator-style dictionary: <c>中文=nghĩa1/nghĩa2</c> per line. Keys are normalized like the text
/// (<see cref="ChineseText.NormalizeKey"/>). For each first character it remembers the longest key, so the converter
/// only tries lengths that can match. Not thread-safe; the converter's owner serializes writes against reads.
/// </summary>
public sealed class PhraseDictionary
{
    private readonly Dictionary<string, string> _entries;
    private readonly Dictionary<char, int> _maxLength = new();

    public PhraseDictionary(int capacity = 0) => _entries = new Dictionary<string, string>(capacity, StringComparer.Ordinal);

    public int Count => _entries.Count;

    public IEnumerable<KeyValuePair<string, string>> Entries => _entries;

    public bool TryGet(string key, out string value) => _entries.TryGetValue(key, out value!);

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    /// <summary>The longest key that starts with <paramref name="first"/> (0 when none does).</summary>
    public int MaxLengthFrom(char first) => _maxLength.TryGetValue(first, out var n) ? n : 0;

    /// <summary>Adds or replaces an entry.</summary>
    public void Set(string key, string value)
    {
        if (key.Length == 0) return;
        _entries[key] = value;
        Grow(key);
    }

    /// <summary>Adds an entry unless the key is already there (the first line of a file wins, like QuickTranslator).</summary>
    public bool TryAdd(string key, string value)
    {
        if (key.Length == 0 || !_entries.TryAdd(key, value)) return false;
        Grow(key);
        return true;
    }

    /// <summary>The longest-key table is only an upper bound, so it is left as it is.</summary>
    public bool Remove(string key) => _entries.Remove(key);

    /// <summary>Reads lines into this dictionary; existing keys keep their value.</summary>
    public int Load(string text)
    {
        var added = 0;
        foreach (var (key, value) in ParseLines(text))
            if (TryAdd(key, value)) added++;
        return added;
    }

    public static PhraseDictionary Parse(string text)
    {
        // Roughly one entry per 40 characters in the usual files.
        var dictionary = new PhraseDictionary(Math.Min(text.Length / 40, 4_000_000));
        dictionary.Load(text);
        return dictionary;
    }

    /// <summary>The <c>key=value</c> pairs of a file; blank lines, lines without "=" and empty keys are skipped.</summary>
    public static IEnumerable<(string Key, string Value)> ParseLines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text.AsSpan(start, end - start).TrimEnd('\r');
            start = end + 1;
            if (line.Length > 0 && line[0] == '﻿') line = line[1..];
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = ChineseText.NormalizeKey(line[..eq].ToString());
            if (key.Length == 0) continue;
            yield return (key, line[(eq + 1)..].Trim().ToString());
        }
    }

    /// <summary>The file form: one <c>key=value</c> per line, keys in ordinal order so diffs stay small.</summary>
    public static string Format(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            sb.Append(key).Append('=').Append(value).Append('\n');
        return sb.ToString();
    }

    private void Grow(string key)
    {
        var first = key[0];
        if (!_maxLength.TryGetValue(first, out var n) || key.Length > n) _maxLength[first] = key.Length;
    }
}
