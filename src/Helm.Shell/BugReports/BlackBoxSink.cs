using Helm.Core.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Helm.Shell.BugReports;

/// <summary>
/// Helm's log into the black box: information and up (warnings and errors as <see cref="BlackBoxKind.Error"/>, with the
/// exception's stack). Added to the app's Serilog logger: <c>.WriteTo.Sink(new BlackBoxSink())</c>.
/// </summary>
public sealed class BlackBoxSink(BlackBox? box = null) : ILogEventSink
{
    private readonly BlackBox _box = box ?? BlackBox.Shared;

    // As the log file writes it: strings without quotes ({Message:lj}).
    private static readonly MessageTemplateTextFormatter Message = new("{Message:lj}");

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;
        try
        {
            var source = logEvent.Properties.TryGetValue("SourceContext", out var ctx) && ctx is ScalarValue { Value: string s }
                ? s[(s.LastIndexOf('.') + 1)..] : null;
            var level = logEvent.Level switch
            {
                LogEventLevel.Information => "INF",
                LogEventLevel.Warning => "WRN",
                LogEventLevel.Error => "ERR",
                _ => "FTL",
            };
            using var rendered = new StringWriter();
            Message.Format(logEvent, rendered);
            var text = $"[{level}] {rendered}";
            if (logEvent.Exception is { } ex) text += $": {ex.GetType().Name}: {ex.Message}";
            var kind = logEvent.Level >= LogEventLevel.Warning ? BlackBoxKind.Error : BlackBoxKind.Log;
            _box.Record(kind, text, source, logEvent.Exception?.ToString());
        }
        catch (Exception)
        {
            // Never let the black box break logging.
        }
    }
}
