namespace Helm.Modules.Ssh;

/// <summary>
/// The last bytes a session printed, so the terminal can be drawn again when the page shows another server and comes
/// back. When it is full the oldest bytes go; a replay then starts after the first line break, so it does not begin in
/// the middle of a character or an escape sequence. Not thread-safe: <see cref="SshSession"/> locks around it.
/// </summary>
public sealed class TerminalBuffer(int capacity = TerminalBuffer.DefaultCapacity)
{
    public const int DefaultCapacity = 512 * 1024;

    private readonly byte[] _data = new byte[capacity];
    private int _start;
    private int _count;
    private bool _wrapped;

    public int Count => _count;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= _data.Length)
        {
            bytes[^_data.Length..].CopyTo(_data);
            _start = 0;
            _count = _data.Length;
            _wrapped = true;
            return;
        }
        var overflow = _count + bytes.Length - _data.Length;
        if (overflow > 0)
        {
            _start = (_start + overflow) % _data.Length;
            _count -= overflow;
            _wrapped = true;
        }
        var end = (_start + _count) % _data.Length;
        var first = Math.Min(bytes.Length, _data.Length - end);
        bytes[..first].CopyTo(_data.AsSpan(end));
        bytes[first..].CopyTo(_data);
        _count += bytes.Length;
    }

    /// <summary>What to replay: everything kept, or after the first line break once old output was dropped.</summary>
    public byte[] Snapshot()
    {
        var all = new byte[_count];
        var first = Math.Min(_count, _data.Length - _start);
        _data.AsSpan(_start, first).CopyTo(all);
        _data.AsSpan(0, _count - first).CopyTo(all.AsSpan(first));
        if (!_wrapped) return all;
        var newline = Array.IndexOf(all, (byte)'\n');
        return newline < 0 ? all : all[(newline + 1)..];
    }

    public void Clear()
    {
        _start = _count = 0;
        _wrapped = false;
    }
}
