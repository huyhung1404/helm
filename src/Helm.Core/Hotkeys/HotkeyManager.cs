using Helm.Core.Interop;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Helm.Core.Hotkeys;

public interface IHotkeyManager
{
    /// <summary>
    /// Registers a global hotkey. Never throws: check <see cref="HotkeyRegistration.IsRegistered"/> /
    /// <see cref="HotkeyRegistration.Error"/>. The callback runs on the hotkey thread and must return quickly.
    /// </summary>
    Task<HotkeyRegistration> TryRegisterAsync(HotkeyDefinition definition, Action callback);

    IReadOnlyList<HotkeyConflict> Conflicts { get; }

    /// <summary>Raised (on a background thread) whenever <see cref="Conflicts"/> may have changed.</summary>
    event EventHandler? ConflictsChanged;
}

public sealed class HotkeyRegistration : IDisposable
{
    private readonly HotkeyManager? _owner;

    internal HotkeyRegistration(HotkeyManager? owner, int id, HotkeyDefinition definition, Action callback)
    {
        _owner = owner;
        Id = id;
        Definition = definition;
        Callback = callback;
    }

    public HotkeyDefinition Definition { get; }
    public bool IsRegistered { get; internal set; }
    public string? Error { get; internal set; }
    internal int Id { get; }
    internal Action Callback { get; }

    public void Dispose() => _owner?.Unregister(this);
}

/// <summary>RegisterHotKey/UnregisterHotKey on a dedicated message-only window with its own message loop.</summary>
public sealed class HotkeyManager : IHotkeyManager, IDisposable
{
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private readonly ILogger<HotkeyManager> _logger;
    private readonly Lazy<MessageLoopThread> _thread;
    private readonly Dictionary<int, HotkeyRegistration> _registrations = new(); // guarded by _registrations
    private IReadOnlyList<HotkeyConflict> _conflicts = [];
    private int _nextId = 1;
    private bool _suspended;

    public HotkeyManager(ILogger<HotkeyManager> logger)
    {
        _logger = logger;
        _thread = new Lazy<MessageLoopThread>(() =>
        {
            var t = new MessageLoopThread("Helm.Hotkeys", logger);
            t.MessageReceived += OnMessage;
            return t;
        });
        HotkeyRecording.RecordingChanged += OnRecordingChanged;
    }

    public IReadOnlyList<HotkeyConflict> Conflicts => Volatile.Read(ref _conflicts);

    public event EventHandler? ConflictsChanged;

    public async Task<HotkeyRegistration> TryRegisterAsync(HotkeyDefinition definition, Action callback)
    {
        HotkeyRegistration registration;
        lock (_registrations)
        {
            registration = new HotkeyRegistration(this, _nextId++, definition, callback);
            _registrations[registration.Id] = registration;
        }

        if (definition.Gesture.IsEmpty)
        {
            registration.Error = "No shortcut assigned.";
        }
        else
        {
            await _thread.Value.InvokeAsync(() => { if (!_suspended) RegisterNative(registration); }).ConfigureAwait(false);
        }

        RecomputeConflicts();
        return registration;
    }

    internal void Unregister(HotkeyRegistration registration)
    {
        lock (_registrations)
        {
            if (!_registrations.Remove(registration.Id)) return;
        }

        if (_thread.IsValueCreated)
            _thread.Value.Post(() => UnregisterNative(registration));
        RecomputeConflicts();
    }

    public void Dispose()
    {
        HotkeyRecording.RecordingChanged -= OnRecordingChanged;
        if (!_thread.IsValueCreated) return;
        List<HotkeyRegistration> all;
        lock (_registrations) all = _registrations.Values.ToList();
        _thread.Value.Post(() => { foreach (var r in all) UnregisterNative(r); });
        _thread.Value.Dispose();
    }

    private void RegisterNative(HotkeyRegistration r)
    {
        var mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        var g = r.Definition.Gesture;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= HOT_KEY_MODIFIERS.MOD_WIN;

        if (PInvoke.RegisterHotKey(new(_thread.Value.WindowHandle), r.Id, mods, (uint)g.Key))
        {
            r.IsRegistered = true;
            r.Error = null;
            _logger.LogInformation("Registered hotkey {Gesture} for {Key}", g, r.Definition.Key);
            return;
        }

        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        r.IsRegistered = false;
        r.Error = error == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"{g} is already in use by another application."
            : $"Could not register {g} (Win32 error {error}).";
        _logger.LogWarning("Failed to register hotkey {Gesture} for {Key}: {Error}", g, r.Definition.Key, r.Error);
    }

    private void UnregisterNative(HotkeyRegistration r)
    {
        if (!r.IsRegistered) return;
        PInvoke.UnregisterHotKey(new(_thread.Value.WindowHandle), r.Id);
        r.IsRegistered = false;
    }

    private bool OnMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message != PInvoke.WM_HOTKEY) return false;

        HotkeyRegistration? r;
        lock (_registrations) _registrations.TryGetValue((int)wParam, out r);
        if (r is null) return true;

        try { r.Callback(); }
        catch (Exception ex) { _logger.LogError(ex, "Hotkey callback for {Key} failed", r.Definition.Key); }
        return true;
    }

    private void OnRecordingChanged(object? sender, bool recording)
    {
        if (!_thread.IsValueCreated) return;
        _thread.Value.Post(() =>
        {
            _suspended = recording;
            List<HotkeyRegistration> all;
            lock (_registrations) all = _registrations.Values.ToList();
            foreach (var r in all)
            {
                if (recording) UnregisterNative(r);
                else if (!r.Definition.Gesture.IsEmpty) RegisterNative(r);
            }
            if (!recording) RecomputeConflicts();
        });
    }

    private void RecomputeConflicts()
    {
        List<HotkeyRegistration> all;
        lock (_registrations) all = _registrations.Values.ToList();
        var failed = all.Where(r => !r.IsRegistered && r.Error is not null && !r.Definition.Gesture.IsEmpty)
            .Select(r => r.Definition.Key).ToHashSet();
        Volatile.Write(ref _conflicts, HotkeyConflictDetector.Detect(all.Select(r => r.Definition), failed));
        ConflictsChanged?.Invoke(this, EventArgs.Empty);
    }
}
