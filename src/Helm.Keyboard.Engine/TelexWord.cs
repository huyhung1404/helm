using System.Text;

namespace Helm.Keyboard;

/// <summary>
/// The word being typed: the input method keeps it as the app's composition text and hands it over (commits it)
/// when a non-letter is typed. Every key recomputes the whole word, so the app only ever sees the finished text and
/// never a Backspace sent to fix a letter it already has.
/// </summary>
public sealed class TelexWord(TelexOptions options)
{
    private readonly StringBuilder _keys = new();
    /// <summary>Text the Telex keys can no longer change, kept in front of the word (rare: see <see cref="Backspace"/>).</summary>
    private string _fixed = "";

    public TelexOptions Options { get; set; } = options;

    public string Text { get; private set; } = "";

    public bool IsEmpty => Text.Length == 0;

    public void Type(char key)
    {
        _keys.Append(key);
        Update();
    }

    /// <summary>
    /// Takes the last letter off the word as it is shown ("việt" → "việ", then "t" gives "việt" again).
    /// False when the word is already empty, so the app gets the Backspace itself.
    /// </summary>
    public bool Backspace()
    {
        if (Text.Length == 0) return false;
        var target = Text[..^1];
        var tail = target.StartsWith(_fixed, StringComparison.Ordinal) ? target[_fixed.Length..] : null;
        if (tail is null || _keys.Length == 0)
        {
            SetFixed(target);
        }
        else if (tail.Length == 0)
        {
            _keys.Clear();
        }
        else if (Telex.Transform(_keys.ToString(0, _keys.Length - 1), Options) == tail)
        {
            _keys.Length--;
        }
        else if (Telex.Encode(tail) is { } keys && Telex.Transform(keys, Options) == tail)
        {
            _keys.Clear().Append(keys);
        }
        else
        {
            SetFixed(target);
        }
        Update();
        return true;
    }

    public void Clear()
    {
        _keys.Clear();
        _fixed = "";
        Text = "";
    }

    private void SetFixed(string text)
    {
        _fixed = text;
        _keys.Clear();
    }

    private void Update() => Text = _fixed + Telex.Transform(_keys.ToString(), Options);
}
