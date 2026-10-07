using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Helm.Modules.Ssh;

/// <summary>What a terminal can show: what was printed so far, then what comes next (a session's shell, or its <see cref="AgentConsole"/>).</summary>
public interface ITerminalSource
{
    /// <summary>
    /// Makes <paramref name="sink"/> the receiver of output from now on and returns what was printed so far, atomically.
    /// The sink may be called on a background thread inside a lock: it must only queue the bytes.
    /// </summary>
    byte[] Attach(Action<byte[]> sink);

    void Detach(Action<byte[]> sink);
}

/// <summary>
/// What AI agents ran on one session (ssh_exec, ssh_menu_run), for the page's AI agent tab: each run's command, its
/// output as it arrives and how it ended, drawn like a terminal. It only shows: the runs go on Helm's own channels,
/// never through the shell, so the user's terminal keeps working beside them. Thread-safe; <see cref="Changed"/> is
/// raised on any thread.
/// </summary>
public sealed class AgentConsole : ITerminalSource
{
    public const int Capacity = 256 * 1024;

    private readonly object _gate = new();
    private readonly TerminalBuffer _buffer = new(Capacity);
    private readonly List<AgentRun> _running = [];
    private Action<byte[]>? _sink;
    private int _runs;
    private bool _atLineStart = true;

    /// <summary>An agent ran something on this session (the tab is worth showing).</summary>
    public bool HasRuns
    {
        get
        {
            lock (_gate) return _runs > 0;
        }
    }

    /// <summary>How many runs are going on now.</summary>
    public int Running
    {
        get
        {
            lock (_gate) return _running.Count;
        }
    }

    /// <summary>A run started or ended, or the console was cleared. Raised on any thread.</summary>
    public event EventHandler? Changed;

    public byte[] Attach(Action<byte[]> sink)
    {
        lock (_gate)
        {
            _sink = sink;
            return _buffer.Snapshot();
        }
    }

    public void Detach(Action<byte[]> sink)
    {
        lock (_gate)
        {
            if (_sink == sink) _sink = null;
        }
    }

    /// <summary>
    /// Shows a run starting. <paramref name="stop"/> is cancelled when the user stops the agent's runs here. End the run
    /// with <see cref="AgentRun.End"/>; disposing it without that shows it as broken off.
    /// </summary>
    /// <param name="title">A menu item's title; null for a shell command.</param>
    public AgentRun Begin(string? title, string command, CancellationTokenSource stop)
    {
        var run = new AgentRun(this, stop);
        var header = new StringBuilder();
        header.Append(Dim(DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · AI agent" + (title is null ? "" : " · " + Printable(title))));
        header.Append("\r\n");
        var lines = command.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++) header.Append("\u001b[1;36m").Append(i == 0 ? "$ " : "> ").Append(Printable(lines[i])).Append("\u001b[0m\r\n");
        lock (_gate)
        {
            _running.Add(run);
            _runs++;
            // A blank line between runs.
            WriteLocked((_buffer.Count > 0 ? (_atLineStart ? "\r\n" : "\r\n\r\n") : "") + header);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return run;
    }

    /// <summary>Stops every run going on now (the agent is told it was stopped in Helm).</summary>
    public void StopAll()
    {
        List<AgentRun> running;
        lock (_gate) running = [.. _running];
        foreach (var run in running) run.Stop();
    }

    /// <summary>Empties what is shown; runs going on keep printing.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _atLineStart = true;
            // "\e[3J" drops the scrollback too.
            _sink?.Invoke("\u001b[2J\u001b[3J\u001b[H"u8.ToArray());
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Output(string text)
    {
        if (text.Length == 0) return;
        // No terminal on Helm's channel: lines end in a bare \n, which a terminal draws as a staircase.
        lock (_gate) WriteLocked(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    internal void Finish(AgentRun run, string footer)
    {
        bool removed;
        lock (_gate)
        {
            removed = _running.Remove(run);
            if (removed) WriteLocked((_atLineStart ? "" : "\r\n") + footer + "\r\n");
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void WriteLocked(string text)
    {
        if (text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        _buffer.Append(bytes);
        _atLineStart = text[^1] == '\n';
        _sink?.Invoke(bytes);
    }

    internal static string Dim(string text) => "\u001b[2m" + text + "\u001b[0m";

    /// <summary>The command as text: control characters (an escape sequence could redraw the screen) become '?'.</summary>
    internal static string Printable(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) && chars[i] != '\t') chars[i] = '?';
        }
        return new string(chars);
    }
}

/// <summary>One run in an <see cref="AgentConsole"/>.</summary>
public sealed class AgentRun : IDisposable
{
    private readonly AgentConsole _console;
    private readonly CancellationTokenSource _stop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _ended;

    internal AgentRun(AgentConsole console, CancellationTokenSource stop)
    {
        _console = console;
        _stop = stop;
    }

    /// <summary>The user stopped it in Helm.</summary>
    public bool StoppedByUser { get; private set; }

    /// <summary>Shows output as it arrives (any thread).</summary>
    public void Output(string text)
    {
        if (Volatile.Read(ref _ended) == 0) _console.Output(text);
    }

    /// <summary>Shows how the run ended.</summary>
    /// <param name="cancelled">The agent called it off (its client cancelled the call).</param>
    public void End(MenuRunResult result, bool timedOut, int timeoutSeconds, bool cancelled = false)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        var took = AgentConsole.Dim(" · " + Duration(_clock.Elapsed));
        var error = result.Error.Length > 0 && result.Error != "Stopped." ? "\u001b[31m" + Lines(result.Error) + "\u001b[0m\r\n" : "";
        string footer;
        if (StoppedByUser) footer = "\u001b[33m■ Stopped in Helm\u001b[0m" + took;
        else if (cancelled) footer = "\u001b[33m■ Called off by the agent\u001b[0m" + took;
        else if (timedOut) footer = $"\u001b[33m■ Stopped after {timeoutSeconds} s (the agent's time limit)\u001b[0m" + took;
        else if (result.ExitCode is null) footer = error + "\u001b[31m✗ Broken off\u001b[0m" + took;
        else if (result.ExitCode == 0) footer = error + "\u001b[32m✓ exit 0\u001b[0m" + took;
        else footer = error + $"\u001b[31m✗ exit {result.ExitCode}\u001b[0m" + took;
        _console.Finish(this, footer);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0) _console.Finish(this, "\u001b[31m✗ Broken off\u001b[0m");
    }

    internal void Stop()
    {
        if (Volatile.Read(ref _ended) != 0) return;
        StoppedByUser = true;
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already over.
        }
    }

    private static string Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string Duration(TimeSpan t) => t.TotalSeconds < 60
        ? t.TotalSeconds.ToString(t.TotalSeconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s"
        : $"{(int)t.TotalMinutes} min {t.Seconds} s";
}
