using Helm.Core.Interop;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Helm.Core.Hooks;

public static class WinEvents
{
    public const uint SystemForeground = 0x0003;
    public const uint SystemMoveSizeStart = 0x000A;
    public const uint SystemMoveSizeEnd = 0x000B;
    public const uint SystemMinimizeStart = 0x0016;
    public const uint SystemMinimizeEnd = 0x0017;
    public const uint ObjectCreate = 0x8000;
    public const uint ObjectDestroy = 0x8001;
    public const uint ObjectShow = 0x8002;
    public const uint ObjectHide = 0x8003;
    public const uint ObjectCloaked = 0x8017;
    public const uint ObjectUncloaked = 0x8018;
    public const uint ObjectLocationChange = 0x800B;
}

public readonly record struct WinEventArgs(uint Event, nint Hwnd, int ObjectId, int ChildId, uint Time)
{
    /// <summary>OBJID_WINDOW + CHILDID_SELF: the event is about the window itself (not a caret, cursor, child…).</summary>
    public bool IsWindowEvent => ObjectId == 0 && ChildId == 0 && Hwnd != 0;
}

/// <summary>
/// SetWinEventHook (out of context, skipping our own process). The hook is installed on — and callbacks arrive on —
/// the given <see cref="MessageLoopThread"/>, so a module can own its overlays on the same thread without marshaling.
/// </summary>
public sealed class WinEventHook : IDisposable
{
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly MessageLoopThread _thread;
    private readonly Action<WinEventArgs> _callback;
    private readonly ILogger? _logger;
    private readonly WINEVENTPROC _proc; // rooted
    private readonly List<HWINEVENTHOOK> _hooks = [];

    private WinEventHook(MessageLoopThread thread, Action<WinEventArgs> callback, ILogger? logger)
    {
        _thread = thread;
        _callback = callback;
        _logger = logger;
        _proc = OnEvent;
    }

    /// <summary>Installs one hook per (min, max) range on <paramref name="thread"/>.</summary>
    public static async Task<WinEventHook> InstallAsync(
        MessageLoopThread thread,
        IEnumerable<(uint Min, uint Max)> ranges,
        Action<WinEventArgs> callback,
        ILogger? logger = null)
    {
        var hook = new WinEventHook(thread, callback, logger);
        var list = ranges.ToList();
        await thread.InvokeAsync(() =>
        {
            foreach (var (min, max) in list)
            {
                var h = PInvoke.SetWinEventHook(min, max, default, hook._proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                if (h.IsNull) throw new HookInstallException($"SetWinEventHook(0x{min:X4}-0x{max:X4}) failed.");
                hook._hooks.Add(h);
            }
        }).ConfigureAwait(false);
        return hook;
    }

    public static Task<WinEventHook> InstallAsync(MessageLoopThread thread, uint[] events, Action<WinEventArgs> callback, ILogger? logger = null) =>
        InstallAsync(thread, events.Select(e => (e, e)), callback, logger);

    public void Dispose()
    {
        if (_hooks.Count == 0) return;
        var hooks = _hooks.ToList();
        _hooks.Clear();
        var unhook = () => { foreach (var h in hooks) PInvoke.UnhookWinEvent(h); };
        if (_thread.IsCurrentThread) unhook();
        else _thread.InvokeAsync(unhook).Wait(TimeSpan.FromSeconds(2));
    }

    private void OnEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            _callback(new WinEventArgs(@event, hwnd, idObject, idChild, time));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WinEvent callback failed for event 0x{Event:X4}", @event);
        }
    }
}
