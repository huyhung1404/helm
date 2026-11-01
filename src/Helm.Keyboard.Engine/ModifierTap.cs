namespace Helm.Typing;

/// <summary>
/// A shortcut made of modifier keys only, pressed together and let go (left Ctrl+Shift, right Ctrl…), fed with every key
/// going down and up. Another key pressed while they are held makes it that key's shortcut instead (Ctrl+Shift+T), and
/// it fires once, when the first of them is let go.
/// </summary>
public sealed class ModifierTap(params int[] keys)
{
    private readonly HashSet<int> _held = [];
    private bool _together;
    private bool _spoiled;

    public IReadOnlyList<int> Keys { get; } = keys;

    /// <summary>One key going down or up (repeats included); true when it completes the shortcut.</summary>
    public bool OnKey(int virtualKey, bool down)
    {
        var mine = Keys.Contains(virtualKey);
        if (down)
        {
            if (!mine)
            {
                if (_held.Count > 0) _spoiled = true;
                return false;
            }
            _held.Add(virtualKey);
            if (_held.Count == Keys.Count) _together = true;
            return false;
        }
        if (!mine) return false;
        var fires = _together && !_spoiled && _held.Count == Keys.Count;
        _held.Remove(virtualKey);
        if (fires) _spoiled = true; // the other keys going up don't fire it again
        if (_held.Count == 0) _together = _spoiled = false;
        return fires;
    }
}
