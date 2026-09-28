using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Interop;

/// <summary>Handles a window message delivered to a <see cref="MessageLoopThread"/>. Return true when handled.</summary>
public delegate bool WindowMessageHandler(uint message, nint wParam, nint lParam, out nint result);

/// <summary>
/// A dedicated STA thread running a Win32 message loop with its own message-only window (HWND_MESSAGE).
/// Low-level hooks, WinEvent hooks, hotkeys and GDI overlay windows are installed on such threads so their
/// callbacks never depend on the WPF dispatcher being responsive.
/// </summary>
public sealed class MessageLoopThread : IDisposable
{
    private const uint WM_INVOKE = PInvoke.WM_APP + 0x100;
    private const string ClassName = "Helm.MessageLoopWindow";

    private static readonly WNDPROC s_wndProc = StaticWndProc; // rooted: the native class keeps a pointer to it
    private static readonly ConcurrentDictionary<nint, MessageLoopThread> s_byHwnd = new();
    private static readonly object s_classLock = new();
    private static bool s_classRegistered;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dictionary<nuint, Action> _timers = new();
    private readonly Thread _thread;
    private readonly ILogger? _logger;
    private nuint _nextTimerId = 1;
    private HWND _hwnd;
    private volatile bool _disposed;

    public MessageLoopThread(string name, ILogger? logger = null)
    {
        Name = name;
        _logger = logger;
        using var ready = new ManualResetEventSlim();
        Exception? startError = null;
        _thread = new Thread(() => Run(ready, e => startError = e))
        {
            Name = name,
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (startError is not null) throw new InvalidOperationException($"Failed to start message loop thread '{name}'.", startError);
    }

    public string Name { get; }

    /// <summary>The message-only window owned by this thread; hotkeys are registered against it.</summary>
    public nint WindowHandle => _hwnd;

    public int ManagedThreadId => _thread.ManagedThreadId;

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Raised on this thread for every message sent to <see cref="WindowHandle"/> that is not internal.</summary>
    public event WindowMessageHandler? MessageReceived;

    /// <summary>Queues <paramref name="action"/> to run on this thread. Never blocks.</summary>
    public void Post(Action action)
    {
        if (_disposed) return;
        _queue.Enqueue(action);
        PInvoke.PostMessage(_hwnd, WM_INVOKE, default, default);
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (IsCurrentThread)
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            tcs.SetException(new ObjectDisposedException(Name));
            return tcs.Task;
        }

        Post(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Starts a repeating WM_TIMER on this thread. Must be called on this thread. Dispose the result to stop it.</summary>
    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        VerifyAccess();
        var id = _nextTimerId++;
        _timers[id] = tick;
        PInvoke.SetTimer(_hwnd, id, (uint)Math.Max(10, interval.TotalMilliseconds), null);
        return new TimerHandle(this, id);
    }

    public void VerifyAccess()
    {
        if (!IsCurrentThread) throw new InvalidOperationException($"This operation must run on the '{Name}' thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PInvoke.PostMessage(_hwnd, PInvoke.WM_CLOSE, default, default);
        if (!IsCurrentThread && !_thread.Join(TimeSpan.FromSeconds(3)))
            _logger?.LogWarning("Message loop thread {Name} did not exit in time", Name);
    }

    private void Run(ManualResetEventSlim ready, Action<Exception> reportError)
    {
        try
        {
            EnsureClassRegistered();
            unsafe
            {
                _hwnd = PInvoke.CreateWindowEx(0, ClassName, Name, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, default, default, null);
            }
            if (_hwnd.IsNull) throw new System.ComponentModel.Win32Exception();
            s_byHwnd[_hwnd] = this;
        }
        catch (Exception ex)
        {
            reportError(ex);
            ready.Set();
            return;
        }

        ready.Set();

        while (true)
        {
            var result = PInvoke.GetMessage(out var msg, default, 0, 0);
            if (result.Value == 0 || result.Value == -1) break;
            PInvoke.TranslateMessage(msg);
            PInvoke.DispatchMessage(msg);
        }

        s_byHwnd.TryRemove(_hwnd, out _);
        _logger?.LogDebug("Message loop thread {Name} exited", Name);
    }

    private void Drain()
    {
        while (_queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { _logger?.LogError(ex, "Unhandled exception in work item on {Name}", Name); }
        }
    }

    private static unsafe void EnsureClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_classRegistered) return;
            fixed (char* className = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = s_wndProc,
                    hInstance = PInvoke.GetModuleHandle((string?)null),
                    lpszClassName = className,
                };
                if (PInvoke.RegisterClassEx(wc) == 0) throw new System.ComponentModel.Win32Exception();
            }
            s_classRegistered = true;
        }
    }

    private static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (!s_byHwnd.TryGetValue(hwnd, out var self))
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_INVOKE:
                self.Drain();
                return default;
            case PInvoke.WM_TIMER:
                if (self._timers.TryGetValue(wParam.Value, out var tick))
                {
                    try { tick(); }
                    catch (Exception ex) { self._logger?.LogError(ex, "Timer callback failed on {Name}", self.Name); }
                }
                return default;
            case PInvoke.WM_CLOSE:
                self.Drain();
                foreach (var id in self._timers.Keys) PInvoke.KillTimer(hwnd, id);
                self._timers.Clear();
                PInvoke.DestroyWindow(hwnd);
                return default;
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return default;
        }

        var handlers = self.MessageReceived;
        if (handlers is not null)
        {
            foreach (WindowMessageHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    if (handler(msg, (nint)wParam.Value, lParam.Value, out var result)) return new LRESULT(result);
                }
                catch (Exception ex)
                {
                    self._logger?.LogError(ex, "Message handler failed on {Name}", self.Name);
                }
            }
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private sealed class TimerHandle(MessageLoopThread owner, nuint id) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Post(() =>
            {
                if (owner._timers.Remove(id)) PInvoke.KillTimer(owner._hwnd, id);
            });
        }
    }
}
