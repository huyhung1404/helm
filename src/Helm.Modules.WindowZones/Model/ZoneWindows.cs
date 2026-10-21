namespace Helm.Modules.WindowZones.Model;

/// <summary>A window in a zone and the size it had before it was dropped there.</summary>
public sealed record ZoneMember(nint Handle, int ProcessId, int OriginalWidth, int OriginalHeight);

/// <summary>What the zone shortcut does, given the state of the zone's windows.</summary>
public enum ZoneToggleAction
{
    /// <summary>No windows in the zone: show where it is and how to fill it.</summary>
    Empty,

    /// <summary>Every window is minimized (the zone is closed): bring them all back.</summary>
    Open,

    /// <summary>Open, but another app is in front: bring the zone's window to the front.</summary>
    BringToFront,

    /// <summary>Open and in front: minimize all of its windows.</summary>
    Close,
}

/// <summary>
/// Which windows are in which zone — each zone a stack of windows sharing its rectangle — and which of them was last
/// in front. Pure bookkeeping: the engine owns one instance on its thread and does the Win32 work.
/// </summary>
public sealed class ZoneWindows
{
    private readonly Dictionary<string, Stack> _zones = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, string> _zoneOf = [];

    public bool IsEmpty => _zoneOf.Count == 0;

    public static ZoneToggleAction Decide(int windows, int minimized, bool zoneIsInFront)
    {
        if (windows == 0) return ZoneToggleAction.Empty;
        if (minimized >= windows) return ZoneToggleAction.Open;
        return zoneIsInFront ? ZoneToggleAction.Close : ZoneToggleAction.BringToFront;
    }

    public string? ZoneOf(nint handle) => _zoneOf.GetValueOrDefault(handle);

    /// <summary>The zone's windows in the order they were added (the order the switch shortcut visits them).</summary>
    public IReadOnlyList<ZoneMember> Members(string zoneId) =>
        _zones.TryGetValue(zoneId, out var stack) ? stack.Windows : [];

    public ZoneMember? Find(nint handle) =>
        _zoneOf.TryGetValue(handle, out var zone) ? _zones[zone].Windows.First(m => m.Handle == handle) : null;

    /// <summary>The window that comes back on top when the zone opens: the last one in front, else the newest.</summary>
    public nint Top(string zoneId)
    {
        if (!_zones.TryGetValue(zoneId, out var stack) || stack.Windows.Count == 0) return 0;
        return stack.Windows.Any(m => m.Handle == stack.LastActive) ? stack.LastActive : stack.Windows[^1].Handle;
    }

    /// <summary>Puts a window in a zone (taking it out of any other) and makes it the zone's top window.</summary>
    public void Add(string zoneId, ZoneMember member)
    {
        if (_zoneOf.TryGetValue(member.Handle, out var current))
        {
            if (current == zoneId)
            {
                _zones[zoneId].LastActive = member.Handle;
                return;
            }
            Remove(member.Handle);
        }
        if (!_zones.TryGetValue(zoneId, out var stack)) _zones[zoneId] = stack = new Stack();
        stack.Windows.Add(member);
        stack.LastActive = member.Handle;
        _zoneOf[member.Handle] = zoneId;
    }

    public bool Remove(nint handle)
    {
        if (!_zoneOf.Remove(handle, out var zoneId)) return false;
        var stack = _zones[zoneId];
        stack.Windows.RemoveAll(m => m.Handle == handle);
        if (stack.LastActive == handle) stack.LastActive = 0;
        if (stack.Windows.Count == 0) _zones.Remove(zoneId);
        return true;
    }

    /// <summary>Notes that a window came to the front. True when it is in a zone and became that zone's top window.</summary>
    public bool Touch(nint handle)
    {
        if (!_zoneOf.TryGetValue(handle, out var zoneId)) return false;
        var stack = _zones[zoneId];
        if (stack.LastActive == handle) return false;
        stack.LastActive = handle;
        return true;
    }

    /// <summary>Lets go of the windows of zones that no longer exist. True when any were let go.</summary>
    public bool RetainZones(IReadOnlySet<string> zoneIds)
    {
        var gone = _zones.Keys.Where(id => !zoneIds.Contains(id)).ToList();
        foreach (var id in gone)
        {
            foreach (var m in _zones[id].Windows) _zoneOf.Remove(m.Handle);
            _zones.Remove(id);
        }
        return gone.Count > 0;
    }

    public ZoneWindowsFile Export()
    {
        var file = new ZoneWindowsFile();
        foreach (var (id, stack) in _zones)
        {
            file.Zones[id] = new ZoneMembersRecord
            {
                LastActive = stack.LastActive,
                Windows = stack.Windows.Select(m => new ZoneMemberRecord
                {
                    Handle = m.Handle,
                    ProcessId = m.ProcessId,
                    OriginalWidth = m.OriginalWidth,
                    OriginalHeight = m.OriginalHeight,
                }).ToList(),
            };
        }
        return file;
    }

    /// <summary>
    /// Reads a saved file, keeping only windows <paramref name="isSameWindow"/> confirms still exist (same handle and
    /// same process) and zones in <paramref name="zoneIds"/>.
    /// </summary>
    public static ZoneWindows Import(ZoneWindowsFile file, IReadOnlySet<string> zoneIds, Func<nint, int, bool> isSameWindow)
    {
        var result = new ZoneWindows();
        foreach (var (id, record) in file.Zones)
        {
            if (!zoneIds.Contains(id)) continue;
            foreach (var w in record.Windows)
            {
                var handle = (nint)w.Handle;
                if (handle == 0 || result._zoneOf.ContainsKey(handle) || !isSameWindow(handle, w.ProcessId)) continue;
                result.Add(id, new ZoneMember(handle, w.ProcessId, w.OriginalWidth, w.OriginalHeight));
            }
            if (result._zones.TryGetValue(id, out var stack))
                stack.LastActive = stack.Windows.Any(m => m.Handle == (nint)record.LastActive) ? (nint)record.LastActive : stack.Windows[^1].Handle;
        }
        return result;
    }

    private sealed class Stack
    {
        public List<ZoneMember> Windows { get; } = [];
        public nint LastActive { get; set; }
    }
}
