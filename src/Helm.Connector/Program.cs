using System.Runtime.InteropServices;
using Helm.Connector;

// Ctrl+C and systemd's SIGTERM end a running command (helm serve) cleanly instead of killing the process; a second
// one, while it is still stopping, kills it.
using var stop = new CancellationTokenSource();
void Stop(PosixSignalContext context)
{
    context.Cancel = !stop.IsCancellationRequested;
    stop.Cancel();
}
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

return await ConnectorApp.RunAsync(args, CliConsole.System, stop.Token);
