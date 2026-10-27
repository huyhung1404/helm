using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Helm.Modules.Workspace;

/// <summary>
/// One claude process a VS Code window runs: Claude Code starts one per Claude tab (or side bar) as a child of the
/// window's extension host, whose pid the window's Helm Bridge reports.
/// </summary>
/// <param name="Busy">Claude Code's own word on it: true while a turn runs, false when idle, null when unknown.</param>
/// <param name="StatusAt">When Claude Code last wrote <paramref name="Busy"/>.</param>
public sealed record ClaudeProcess(int Pid, int HostPid, string SessionId, string Cwd, bool? Busy, DateTimeOffset? StatusAt);

/// <summary>
/// Which Claude sessions each VS Code window really has, and whether they work right now: the claude processes under
/// the windows' extension hosts, each named in Claude Code's own <c>~/.claude/sessions/&lt;pid&gt;.json</c>. Exact where
/// the tab names (cut to 25 characters, shared by many conversations) can only be guessed from.
/// </summary>
public sealed class ClaudeProcesses
{
    private readonly Func<IReadOnlyList<(int Pid, int ParentPid, string Name)>> _list;
    private readonly string _sessionsDir;
    // The last good read of each pid: Claude Code rewrites its file in place, so it is sometimes empty for a moment.
    private readonly Dictionary<int, ClaudeProcess> _known = new();

    public ClaudeProcesses(Func<IReadOnlyList<(int Pid, int ParentPid, string Name)>>? list = null, string? sessionsDir = null)
    {
        _list = list ?? ListProcesses;
        _sessionsDir = sessionsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
    }

    /// <summary>The claude processes whose parent is one of <paramref name="hostPids"/> and that name their session.</summary>
    public IReadOnlyList<ClaudeProcess> Scan(IReadOnlyCollection<int> hostPids)
    {
        if (hostPids.Count == 0)
        {
            lock (_known) _known.Clear();
            return [];
        }
        var hosts = hostPids.ToHashSet();
        var found = new List<ClaudeProcess>();
        var alive = new HashSet<int>();
        IReadOnlyList<(int Pid, int ParentPid, string Name)> processes;
        try
        {
            processes = _list();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            return [];
        }
        lock (_known)
        {
            foreach (var (pid, parent, name) in processes)
            {
                if (!hosts.Contains(parent) || !IsClaude(name)) continue;
                alive.Add(pid);
                if (Read(pid, parent) is { } read) _known[pid] = read;
                if (_known.GetValueOrDefault(pid) is { } p && p.HostPid == parent) found.Add(p);
            }
            foreach (var gone in _known.Keys.Where(k => !alive.Contains(k)).ToList()) _known.Remove(gone);
        }
        return found;
    }

    private static bool IsClaude(string name) =>
        name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("claude", StringComparison.OrdinalIgnoreCase);

    private ClaudeProcess? Read(int pid, int host)
    {
        try
        {
            var path = Path.Combine(_sessionsDir, pid + ".json");
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() != pid) return null;
            if (!root.TryGetProperty("sessionId", out var s) || s.GetString() is not { Length: > 0 } session) return null;
            var cwd = root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            bool? busy = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString() switch { "busy" => true, "idle" => false, _ => null }
                : null;
            DateTimeOffset? at = root.TryGetProperty("statusUpdatedAt", out var u) && u.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(u.GetInt64())
                : null;
            return new ClaudeProcess(pid, host, session, cwd, busy, at);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Every process of this session with its parent: one Toolhelp snapshot, no handle to any process.</summary>
    private static IReadOnlyList<(int Pid, int ParentPid, string Name)> ListProcesses()
    {
        using var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot.IsInvalid) return [];
        var list = new List<(int, int, string)>();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            list.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
        return list;
    }

    private const uint SnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
