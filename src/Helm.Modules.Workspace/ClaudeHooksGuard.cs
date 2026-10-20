using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// Keeps Helm's hooks in Claude Code's settings while the tool runs. When another program (or another copy of Helm)
/// rewrites the file without them, or with another port, they are put back: without them the phone silently stops
/// hearing that Claude got a message or finished, and Claude says nothing about a hook it cannot reach.
/// </summary>
public sealed class ClaudeHooksGuard : IDisposable
{
    /// <summary>Checked this often too, in case a change of the file was not seen.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(2);

    /// <summary>Put back at most this often: no tug of war with a program that keeps taking them out.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private readonly string _path;
    private readonly Func<string?, bool> _inPlace;
    private readonly Func<string?> _install;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly FileSystemWatcher? _watcher;
    private readonly ITimer _timer;
    private readonly object _lock = new();
    private DateTimeOffset? _lastPut;
    private bool _disposed;

    /// <param name="path">Claude Code's settings file.</param>
    /// <param name="inPlace">Whether the file's text has Helm's hooks as they should be (or Helm does not manage them now).</param>
    /// <param name="install">Writes them; returns why it could not, or null.</param>
    /// <param name="watch">Look as soon as the file changes (tests turn it off to call <see cref="Check"/> themselves).</param>
    public ClaudeHooksGuard(string path, Func<string?, bool> inPlace, Func<string?> install, ILogger logger, TimeProvider? time = null, bool watch = true)
    {
        _path = path;
        _inPlace = inPlace;
        _install = install;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Check(), null, CheckEvery, CheckEvery);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (watch && Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                _watcher.Changed += (_, _) => Soon();
                _watcher.Created += (_, _) => Soon();
                _watcher.Renamed += (_, _) => Soon();
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Workspace: cannot watch {Path}; checking it every {Every}", path, CheckEvery);
        }
    }

    /// <summary>The hooks were put back; the argument says why it failed, or is null.</summary>
    public event Action<string?>? Restored;

    /// <summary>The file changed: look once it has been written in full (writers often write it in steps).</summary>
    private void Soon()
    {
        lock (_lock)
        {
            if (!_disposed) _timer.Change(Settle, CheckEvery);
        }
    }

    /// <summary>Puts the hooks back if they are gone. True when it wrote them.</summary>
    public bool Check()
    {
        string? problem;
        lock (_lock)
        {
            if (_disposed) return false;
            string? json;
            try
            {
                json = File.Exists(_path) ? File.ReadAllText(_path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being written right now: the next look sees it.
                _timer.Change(Settle, CheckEvery);
                return false;
            }
            if (_inPlace(json)) return false;
            var now = _time.GetUtcNow();
            if (_lastPut is { } last && now - last < MinGap)
            {
                _timer.Change(MinGap - (now - last), CheckEvery);
                return false;
            }
            _lastPut = now;
            _logger.LogWarning("Workspace: Helm's hooks were gone from {Path} (another program rewrote it); putting them back", _path);
            problem = _install();
        }
        Restored?.Invoke(problem);
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
