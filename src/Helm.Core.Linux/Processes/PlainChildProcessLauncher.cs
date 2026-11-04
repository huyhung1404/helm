using System.Diagnostics;

namespace Helm.Core.Processes;

/// <summary>
/// The <see cref="IChildProcessLauncher"/> of helm on Linux: a plain child process with redirected streams, its
/// command line read back into arguments (<see cref="CommandLine.Split"/>). Nothing to drop: helm never runs as root
/// for its agents (the service has its own user), and systemd ends whatever is left of a stopped service's children.
/// </summary>
public sealed class PlainChildProcessLauncher : IChildProcessLauncher
{
    public ChildProcess Start(ChildProcessStartInfo startInfo)
    {
        if (!Directory.Exists(startInfo.WorkingDirectory))
            throw new ChildProcessException($"The folder '{startInfo.WorkingDirectory}' does not exist.");
        var args = CommandLine.Split(startInfo.CommandLine);
        if (args.Count == 0) throw new ChildProcessException("There is no command to start.");

        var info = new ProcessStartInfo(args[0])
        {
            WorkingDirectory = startInfo.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args.Skip(1)) info.ArgumentList.Add(arg);
        if (startInfo.Environment is { } environment)
        {
            info.Environment.Clear();
            foreach (var (key, value) in environment) info.Environment[key] = value;
        }
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new ChildProcessException($"'{args[0]}' did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new ChildProcessException($"'{args[0]}' could not be started: {ex.Message}", ex);
        }
        return new PlainChildProcess(process);
    }

    private sealed class PlainChildProcess : ChildProcess
    {
        private readonly Process _process;
        private readonly Task<int> _exited;
        private int _disposed;

        public PlainChildProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            _exited = WaitAsync(process);
        }

        public override int Id { get; }

        public override Stream StandardInput => _process.StandardInput.BaseStream;
        public override Stream StandardOutput => _process.StandardOutput.BaseStream;
        public override Stream StandardError => _process.StandardError.BaseStream;

        public override bool KillsProcessTree => true;

        public override Task<int> Exited => _exited;

        public override void Kill()
        {
            if (HasExited) return;
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Kill(); } catch { /* already gone */ }
            _process.Dispose();
        }

        private static async Task<int> WaitAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }
}
