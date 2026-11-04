using System.Runtime.InteropServices;
using Helm.Cli;

// Ctrl+C and systemd's SIGTERM end a running command (helm serve) cleanly instead of killing the process.
using var stop = new CancellationTokenSource();
void Stop(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

return await CliApp.RunAsync(args, CliConsole.System, stop.Token);
