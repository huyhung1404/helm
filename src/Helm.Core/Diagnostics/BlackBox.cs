using System.IO.Compression;
using System.Text.Json;
using Helm.Core.Settings;

namespace Helm.Core.Diagnostics;

/// <summary>What a black box event is about.</summary>
public enum BlackBoxKind
{
    /// <summary>The user did something: a click or tap, a shortcut, typing (only how much, never what).</summary>
    Input,

    /// <summary>A page, tab, window or dialog opened or closed.</summary>
    Navigation,

    /// <summary>A log line from Helm (information and up).</summary>
    Log,

    /// <summary>A warning, an error or an exception (with its stack in <see cref="BlackBoxEvent.Detail"/>).</summary>
    Error,

    /// <summary>The UI thread stopped answering for a while.</summary>
    Performance,

    /// <summary>A sync push or pull.</summary>
    Sync,

    /// <summary>A click on something that was disabled (a button that "does nothing").</summary>
    DisabledClick,
}

/// <summary>One thing that happened, at <see cref="At"/>.</summary>
/// <param name="Text">One line for a person, e.g. "Clicked Save". Never what the user typed.</param>
/// <param name="Source">Where: the page, the control, the logger.</param>
/// <param name="Detail">More, e.g. an exception's stack.</param>
public sealed record BlackBoxEvent(DateTimeOffset At, BlackBoxKind Kind, string Text, string? Source = null, string? Detail = null);

/// <summary>
/// The black box at one moment: the events of the <see cref="BlackBox.Window"/> before <see cref="FrozenAt"/>, oldest
/// first, and what Helm knew about itself then (<see cref="Context"/>: version, device, page, memory…).
/// </summary>
public sealed record BlackBoxSnapshot(DateTimeOffset FrozenAt, IReadOnlyList<BlackBoxEvent> Events, IReadOnlyDictionary<string, string> Context)
{
    public int ErrorCount => Events.Count(e => e.Kind == BlackBoxKind.Error);

    public int DisabledClickCount => Events.Count(e => e.Kind == BlackBoxKind.DisabledClick);

    /// <summary>The longest time the UI froze, if it did.</summary>
    public TimeSpan? LongestFreeze => Events.Where(e => e.Kind == BlackBoxKind.Performance)
        .Select(e => BlackBox.FreezeOf(e)).Where(d => d is not null).Max();

    /// <summary>Seconds before <see cref="FrozenAt"/> (negative), for showing "-37.9 s".</summary>
    public double SecondsBefore(BlackBoxEvent e) => (e.At - FrozenAt).TotalSeconds;

    /// <summary>gzip'd JSON, as kept with a bug report.</summary>
    public byte[] ToBytes()
    {
        using var memory = new MemoryStream();
        using (var zip = new GZipStream(memory, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(zip, this, BlackBox.Json);
        return memory.ToArray();
    }

    public static BlackBoxSnapshot FromBytes(byte[] bytes)
    {
        using var zip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<BlackBoxSnapshot>(zip, BlackBox.Json)
            ?? throw new InvalidDataException("Not a black box.");
    }
}

/// <summary>
/// Helm's flight recorder: the last <see cref="Window"/> of what the user did and how Helm answered, in memory only. A
/// bug report freezes it (<see cref="Freeze"/>): what happened in the report window itself is not recorded until it
/// closes (<see cref="Thaw"/>). Only a crash writes it to disk (<see cref="WriteCrash"/>), so the next start can
/// report it. Thread-safe; recording is cheap and never throws.
/// </summary>
/// <remarks>
/// What goes in: the platforms' input and navigation hooks, Helm's log (a Serilog sink), sync and UI stalls
/// (<see cref="UiStallWatch"/>). Never the text the user types, nor anything of a password box or of the Vault.
/// </remarks>
public sealed class BlackBox
{
    /// <summary>How far back the black box remembers.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>At most this many events (a burst of logging cannot use much memory).</summary>
    public const int MaxEvents = 3000;

    internal const int MaxText = 400;
    internal const int MaxDetail = 6000;

    internal static readonly JsonSerializerOptions Json = new(HelmJson.Options) { WriteIndented = false };

    private readonly object _gate = new();
    private readonly Queue<BlackBoxEvent> _events = new();
    private readonly List<(string Name, Func<string?> Value)> _context = [];
    private readonly TimeProvider _time;
    private int _frozen;

    public BlackBox(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The app's one black box. Hooks that start before DI (the log sink, crash handlers) use it.</summary>
    public static BlackBox Shared { get; } = new();

    /// <summary>True while a bug report holds it.</summary>
    public bool IsFrozen => Volatile.Read(ref _frozen) > 0;

    public DateTimeOffset Now => _time.GetUtcNow();

    public void Record(BlackBoxKind kind, string text, string? source = null, string? detail = null)
    {
        if (IsFrozen || string.IsNullOrEmpty(text)) return;
        var e = new BlackBoxEvent(_time.GetUtcNow(), kind, Clip(text, MaxText), source is null ? null : Clip(source, 120), detail is null ? null : Clip(detail, MaxDetail));
        lock (_gate)
        {
            _events.Enqueue(e);
            Trim(e.At);
        }
    }

    public void Input(string text, string? source = null) => Record(BlackBoxKind.Input, text, source);

    public void Navigation(string text, string? source = null) => Record(BlackBoxKind.Navigation, text, source);

    public void Error(string text, string? source = null, Exception? exception = null) =>
        Record(BlackBoxKind.Error, text, source, exception?.ToString());

    /// <summary>
    /// Adds a line to <see cref="BlackBoxSnapshot.Context"/>, read when the box is frozen (the page shown, sync state…).
    /// A value that throws or is null is left out.
    /// </summary>
    public void AddContext(string name, Func<string?> value)
    {
        lock (_gate) _context.Add((name, value));
    }

    /// <summary>The events of the last <see cref="Window"/>, and the context, as they are now.</summary>
    public BlackBoxSnapshot Snapshot()
    {
        var now = _time.GetUtcNow();
        BlackBoxEvent[] events;
        (string, Func<string?>)[] context;
        lock (_gate)
        {
            Trim(now);
            events = [.. _events];
            context = [.. _context];
        }
        var values = new Dictionary<string, string>();
        foreach (var (name, read) in context)
        {
            try
            {
                if (read() is { Length: > 0 } value) values[name] = Clip(value, MaxText);
            }
            catch (Exception)
            {
                // A context line is best effort.
            }
        }
        return new BlackBoxSnapshot(now, events, values);
    }

    /// <summary>
    /// Stops recording and returns what the box holds. Each call needs its <see cref="Thaw"/> (a report window that
    /// closes, sent or not).
    /// </summary>
    public BlackBoxSnapshot Freeze()
    {
        var snapshot = Snapshot();
        Interlocked.Increment(ref _frozen);
        return snapshot;
    }

    public void Thaw()
    {
        if (Interlocked.Decrement(ref _frozen) < 0) Interlocked.Exchange(ref _frozen, 0);
    }

    /// <summary>Writes the box to <paramref name="file"/> as the app goes down (best effort, never throws).</summary>
    public void WriteCrash(string file, Exception? exception)
    {
        try
        {
            if (exception is not null)
                Record(BlackBoxKind.Error, $"Helm crashed: {exception.GetType().Name}: {exception.Message}", "Crash", exception.ToString());
            var snapshot = Snapshot();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, snapshot.ToBytes());
        }
        catch (Exception)
        {
            // Going down anyway.
        }
    }

    /// <summary>The black box a crash left (and deletes the file), or null.</summary>
    public static BlackBoxSnapshot? TakeCrash(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            File.Delete(file);
            return BlackBoxSnapshot.FromBytes(bytes);
        }
        catch (Exception)
        {
            try { File.Delete(file); } catch (Exception) { /* unreadable: forget it */ }
            return null;
        }
    }

    /// <summary>For a <see cref="BlackBoxKind.Performance"/> event made by <see cref="UiStallWatch"/>: how long the UI froze.</summary>
    public static TimeSpan? FreezeOf(BlackBoxEvent e) =>
        e.Kind == BlackBoxKind.Performance && e.Source is { } s && s.StartsWith(UiStallWatch.SourcePrefix, StringComparison.Ordinal)
        && double.TryParse(s.AsSpan(UiStallWatch.SourcePrefix.Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(ms) : null;

    private void Trim(DateTimeOffset now)
    {
        var oldest = now - Window;
        while (_events.Count > 0 && (_events.Count > MaxEvents || _events.Peek().At < oldest)) _events.Dequeue();
    }

    public static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
