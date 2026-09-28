using System.Threading.Channels;
using Helm.Core.Interop;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Hooks;

public class HookInstallException(string message) : Exception(message);

public abstract class HookEventArgs : EventArgs
{
    /// <summary>Set in an <c>Intercept</c> handler to swallow the input (the hook returns 1).</summary>
    public bool Handled { get; set; }

    public bool IsInjected { get; init; }

    /// <summary>dwExtraInfo of the event; <see cref="InputSimulator.HelmSignature"/> marks input Helm injected itself.</summary>
    public nuint ExtraInfo { get; init; }

    public uint Time { get; init; }
}

/// <summary>
/// WH_KEYBOARD_LL / WH_MOUSE_LL on a dedicated STA thread with its own message loop. Installed while at least one
/// <see cref="Acquire"/> handle is alive. <see cref="Intercept"/> runs synchronously inside the hook and must return
/// within a few milliseconds (Windows silently removes slow hooks); <see cref="Observed"/> is delivered later from a
/// <see cref="Channel{T}"/> consumer, for anything heavier.
/// </summary>
public abstract class LowLevelHookBase<TArgs> : IDisposable where TArgs : HookEventArgs
{
    private readonly WINDOWS_HOOK_ID _hookId;
    private readonly HOOKPROC _proc; // rooted so the GC never collects the native callback
    private readonly object _gate = new();
    private readonly Channel<TArgs> _channel = Channel.CreateBounded<TArgs>(new BoundedChannelOptions(2048)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    private MessageLoopThread? _thread;
    private HHOOK _hook;
    private int _refCount;
    private Task? _consumer;

    private protected LowLevelHookBase(WINDOWS_HOOK_ID hookId, string name, ILogger logger)
    {
        _hookId = hookId;
        Name = name;
        Logger = logger;
        _proc = HookProc;
    }

    public string Name { get; }

    protected ILogger Logger { get; }

    public bool IsInstalled
    {
        get { lock (_gate) return !_hook.IsNull; }
    }

    /// <summary>Synchronous, on the hook thread. Keep it tiny; set <see cref="HookEventArgs.Handled"/> to swallow.</summary>
    public event EventHandler<TArgs>? Intercept;

    /// <summary>Asynchronous, on a thread-pool thread, after the input has already been passed on.</summary>
    public event EventHandler<TArgs>? Observed;

    /// <summary>Installs the hook if needed (throws <see cref="HookInstallException"/> on failure). Dispose the handle to release.</summary>
    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_refCount == 0)
            {
                _thread ??= new MessageLoopThread($"Helm.{Name}", Logger);
                var (hook, error) = _thread.InvokeAsync(() =>
                    {
                        var h = PInvoke.SetWindowsHookEx(_hookId, _proc, PInvoke.GetModuleHandle((string?)null), 0);
                        return (h, h.IsNull ? System.Runtime.InteropServices.Marshal.GetLastWin32Error() : 0);
                    })
                    .GetAwaiter().GetResult();
                if (hook.IsNull)
                    throw new HookInstallException($"Could not install the {Name} (Win32 error {error}).");
                _hook = hook;
                _consumer ??= Task.Run(ConsumeAsync);
                Logger.LogInformation("{Hook} installed", Name);
            }
            _refCount++;
        }
        return new Lease(this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _refCount = 0;
            Uninstall();
            _channel.Writer.TryComplete();
            _thread?.Dispose();
            _thread = null;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Converts the native hook data into event args, or null to ignore the message.</summary>
    private protected abstract TArgs? CreateArgs(WPARAM wParam, LPARAM lParam);

    private void Release()
    {
        lock (_gate)
        {
            if (_refCount == 0) return;
            if (--_refCount == 0) Uninstall();
        }
    }

    private void Uninstall()
    {
        if (_hook.IsNull || _thread is null) return;
        var hook = _hook;
        _hook = default;
        _thread.InvokeAsync(() => PInvoke.UnhookWindowsHookEx(hook)).GetAwaiter().GetResult();
        Logger.LogInformation("{Hook} removed", Name);
    }

    private LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            TArgs? args = null;
            try
            {
                args = CreateArgs(wParam, lParam);
                if (args is not null)
                {
                    Intercept?.Invoke(this, args);
                    if (Observed is not null) _channel.Writer.TryWrite(args);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Hook} handler failed", Name);
            }

            if (args is { Handled: true }) return new LRESULT(1);
        }
        return PInvoke.CallNextHookEx(default, code, wParam, lParam);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var args in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { Observed?.Invoke(this, args); }
                catch (Exception ex) { Logger.LogError(ex, "{Hook} observer failed", Name); }
            }
        }
        catch (ChannelClosedException) { }
    }

    private sealed class Lease(LowLevelHookBase<TArgs> owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }
}
