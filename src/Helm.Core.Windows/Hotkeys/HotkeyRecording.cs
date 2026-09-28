namespace Helm.Core.Hotkeys;

/// <summary>
/// Process-wide flag raised while a hotkey picker is capturing keys, so registered global hotkeys and
/// keyboard hooks step aside and the user's keystrokes reach the picker.
/// </summary>
public static class HotkeyRecording
{
    private static int s_depth;

    public static bool IsRecording => Volatile.Read(ref s_depth) > 0;

    public static event EventHandler<bool>? RecordingChanged;

    public static IDisposable Begin()
    {
        if (Interlocked.Increment(ref s_depth) == 1) RecordingChanged?.Invoke(null, true);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref s_depth) == 0) RecordingChanged?.Invoke(null, false);
        }
    }
}
