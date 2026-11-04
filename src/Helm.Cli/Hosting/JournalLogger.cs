using Microsoft.Extensions.Logging;

namespace Helm.Cli.Hosting;

/// <summary>
/// Logs as one line each on stderr, with the syslog priority prefix (<c>&lt;6&gt;</c>…) that systemd's journal reads as
/// the level when helm runs as a service (<c>journalctl -u helm</c>). Exceptions go on the lines after.
/// </summary>
internal sealed class JournalLoggerFactory(TextWriter output, LogLevel minimum = LogLevel.Information) : ILoggerFactory
{
    private readonly object _gate = new();

    internal LogLevel Minimum { get; } = minimum;

    public ILogger CreateLogger(string categoryName) => new JournalLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose() => output.Flush();

    /// <summary>"Debug", "Information"… from $HELM_LOG_LEVEL; Information when unset or unknown.</summary>
    public static LogLevel LevelFrom(string? text) => Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level) ? level : LogLevel.Information;

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var priority = level switch
        {
            LogLevel.Critical => 2,
            LogLevel.Error => 3,
            LogLevel.Warning => 4,
            LogLevel.Information => 6,
            _ => 7,
        };
        lock (_gate)
        {
            output.WriteLine($"<{priority}>{category}: {message}");
            if (exception is not null) output.WriteLine($"<{priority}>{exception.ToString().Replace("\n", $"\n<{priority}>", StringComparison.Ordinal)}");
            output.Flush();
        }
    }

    private sealed class JournalLogger(JournalLoggerFactory factory, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= factory.Minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) factory.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
