using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace Helm.Core.Processes;

/// <summary>What to start: a full command line (already quoted, see <see cref="CommandLine"/>) and its environment.</summary>
public sealed record ChildProcessStartInfo(string CommandLine, string WorkingDirectory)
{
    /// <summary>The complete environment of the child. Null inherits Helm's own environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public interface IChildProcessLauncher
{
    /// <summary>
    /// Starts a hidden child process with redirected stdin/stdout/stderr inside a kill-on-close job object.
    /// When Helm is elevated the child runs with the desktop shell's (unelevated) token, never as administrator.
    /// </summary>
    /// <exception cref="ChildProcessException">The process could not be started.</exception>
    ChildProcess Start(ChildProcessStartInfo startInfo);
}

public sealed class ChildProcessException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ChildProcessLauncher(ILogger<ChildProcessLauncher> logger) : IChildProcessLauncher
{
    public ChildProcess Start(ChildProcessStartInfo startInfo)
    {
        if (!Directory.Exists(startInfo.WorkingDirectory))
            throw new ChildProcessException($"The folder '{startInfo.WorkingDirectory}' does not exist.");

        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return NativeChild.Start(startInfo, elevated, logger);
    }
}

/// <summary>A running child process started by <see cref="ChildProcessLauncher"/>. Disposing kills it and its whole process tree.</summary>
public sealed class ChildProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle? _job;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _waitRegistration;
    private readonly ProcessWaitHandle _waitHandle;
    private int _disposed;

    internal ChildProcess(int id, SafeProcessHandle process, SafeFileHandle? job, Stream stdin, Stream stdout, Stream stderr)
    {
        Id = id;
        _process = process;
        _job = job;
        StandardInput = stdin;
        StandardOutput = stdout;
        StandardError = stderr;
        _waitHandle = new ProcessWaitHandle(process);
        _waitRegistration = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) => _exited.TrySetResult(ReadExitCode()), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int Id { get; }

    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }

    /// <summary>False when the job object could not be set up; then only the direct child is killed on dispose.</summary>
    public bool KillsProcessTree => _job is not null;

    /// <summary>Completes with the exit code when the process ends.</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    public void Kill()
    {
        if (HasExited) return;
        if (_job is not null) PInvoke.TerminateJobObject((HANDLE)_job.DangerousGetHandle(), 1);
        else PInvoke.TerminateProcess((HANDLE)_process.DangerousGetHandle(), 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Kill(); } catch { /* already gone */ }
        _waitRegistration.Unregister(null);
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _job?.Dispose(); // closing the last job handle kills anything left (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
        _waitHandle.Dispose();
        _process.Dispose();
        _exited.TrySetResult(-1);
    }

    private unsafe int ReadExitCode()
    {
        uint code;
        return PInvoke.GetExitCodeProcess((HANDLE)_process.DangerousGetHandle(), &code) ? unchecked((int)code) : -1;
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}

/// <summary>Builds Windows command lines with the quoting rules of CommandLineToArgvW / the MSVC runtime.</summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal unless they precede a quote; then each must be doubled.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}

internal static unsafe class NativeChild
{
    private const uint SW_HIDE = 0;

    public static ChildProcess Start(ChildProcessStartInfo info, bool elevated, ILogger logger)
    {
        var parentEnds = new List<SafeFileHandle>(3);
        var childEnds = new List<SafeFileHandle>(3);
        try
        {
            var (stdinRead, stdinWrite) = CreatePipe(parentWrites: true);
            var (stdoutRead, stdoutWrite) = CreatePipe(parentWrites: false);
            var (stderrRead, stderrWrite) = CreatePipe(parentWrites: false);
            parentEnds.AddRange([stdinWrite, stdoutRead, stderrRead]);
            childEnds.AddRange([stdinRead, stdoutWrite, stderrWrite]);

            var si = new STARTUPINFOW
            {
                cb = (uint)sizeof(STARTUPINFOW),
                dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES | STARTUPINFOW_FLAGS.STARTF_USESHOWWINDOW,
                wShowWindow = (ushort)SW_HIDE,
                hStdInput = (HANDLE)stdinRead.DangerousGetHandle(),
                hStdOutput = (HANDLE)stdoutWrite.DangerousGetHandle(),
                hStdError = (HANDLE)stderrWrite.DangerousGetHandle(),
            };

            var pi = elevated ? StartWithShellToken(info, ref si) : StartDirect(info, ref si);
            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            try
            {
                var job = CreateKillOnCloseJob(process, logger);
                if (PInvoke.ResumeThread(pi.hThread) == uint.MaxValue)
                {
                    var error = Marshal.GetLastWin32Error();
                    PInvoke.TerminateProcess(pi.hProcess, 1);
                    job?.Dispose();
                    throw new ChildProcessException("The process was created but could not be resumed.", new Win32Exception(error));
                }

                // The child owns its ends now; keeping ours open would stop stdout from ever reaching EOF.
                foreach (var h in childEnds) h.Dispose();
                childEnds.Clear();

                return new ChildProcess(
                    (int)pi.dwProcessId,
                    process,
                    job,
                    new FileStream(stdinWrite, FileAccess.Write, 1, isAsync: false),
                    new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false),
                    new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false));
            }
            catch
            {
                process.Dispose();
                throw;
            }
            finally
            {
                PInvoke.CloseHandle(pi.hThread);
            }
        }
        catch
        {
            foreach (var h in parentEnds) h.Dispose();
            throw;
        }
        finally
        {
            foreach (var h in childEnds) h.Dispose();
        }
    }

    private static PROCESS_INFORMATION StartDirect(ChildProcessStartInfo info, ref STARTUPINFOW si)
    {
        var env = BuildEnvironmentBlock(info.Environment);
        var commandLine = (info.CommandLine + '\0').ToCharArray();
        PROCESS_INFORMATION pi;
        fixed (char* cmd = commandLine)
        fixed (char* cwd = info.WorkingDirectory)
        fixed (char* envBlock = env)
        fixed (STARTUPINFOW* psi = &si)
        {
            var ok = PInvoke.CreateProcess(
                null,
                new PWSTR(cmd),
                null,
                null,
                true, // inherit the three pipe ends named in STARTUPINFO (only they are inheritable)
                PROCESS_CREATION_FLAGS.CREATE_SUSPENDED | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                envBlock,
                cwd,
                psi,
                &pi);
            if (!ok) throw new ChildProcessException("The process could not be started.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return pi;
    }

    /// <summary>
    /// Helm runs as administrator; tools it launches for the user must not. Borrow the token of the process that
    /// owns the desktop shell window (explorer.exe, medium integrity) and start the child with it.
    /// </summary>
    private static PROCESS_INFORMATION StartWithShellToken(ChildProcessStartInfo info, ref STARTUPINFOW si)
    {
        var shell = PInvoke.GetShellWindow();
        uint shellPid;
        if (shell.IsNull || PInvoke.GetWindowThreadProcessId(shell, &shellPid) == 0 || shellPid == 0)
            throw new ChildProcessException("The Windows shell (explorer.exe) is not running, so Helm cannot start the process without administrator rights.");

        var shellProcess = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (shellProcess.IsNull) throw Fail("Could not open the shell process");
        HANDLE shellToken = default, token = default;
        try
        {
            if (!PInvoke.OpenProcessToken(shellProcess, TOKEN_ACCESS_MASK.TOKEN_DUPLICATE, &shellToken)) throw Fail("Could not read the shell's token");

            const TOKEN_ACCESS_MASK access = TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_DUPLICATE | TOKEN_ACCESS_MASK.TOKEN_ASSIGN_PRIMARY
                | TOKEN_ACCESS_MASK.TOKEN_ADJUST_DEFAULT | TOKEN_ACCESS_MASK.TOKEN_ADJUST_SESSIONID;
            if (!PInvoke.DuplicateTokenEx(shellToken, access, null, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, &token))
                throw Fail("Could not duplicate the shell's token");

            var env = BuildEnvironmentBlock(info.Environment);
            var commandLine = (info.CommandLine + '\0').ToCharArray();
            PROCESS_INFORMATION pi;
            fixed (char* cmd = commandLine)
            fixed (char* cwd = info.WorkingDirectory)
            fixed (char* envBlock = env)
            fixed (STARTUPINFOW* psi = &si)
            {
                // CREATE_NO_WINDOW is not accepted here; STARTF_USESHOWWINDOW + SW_HIDE keeps the console hidden instead.
                var ok = PInvoke.CreateProcessWithToken(
                    token,
                    0,
                    null,
                    new PWSTR(cmd),
                    PROCESS_CREATION_FLAGS.CREATE_SUSPENDED | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                    envBlock,
                    cwd,
                    psi,
                    &pi);
                if (!ok) throw Fail("The process could not be started without administrator rights");
            }
            return pi;
        }
        finally
        {
            if (!token.IsNull) PInvoke.CloseHandle(token);
            if (!shellToken.IsNull) PInvoke.CloseHandle(shellToken);
            PInvoke.CloseHandle(shellProcess);
        }
    }

    private static SafeFileHandle? CreateKillOnCloseJob(SafeProcessHandle process, ILogger logger)
    {
        var job = PInvoke.CreateJobObject(null, (PCWSTR)null);
        if (job.IsNull)
        {
            logger.LogWarning(new Win32Exception(Marshal.GetLastWin32Error()), "Could not create a job object; only the direct child will be killed");
            return null;
        }
        var handle = new SafeFileHandle(job, ownsHandle: true);

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))
            || !PInvoke.AssignProcessToJobObject(job, (HANDLE)process.DangerousGetHandle()))
        {
            logger.LogWarning(new Win32Exception(Marshal.GetLastWin32Error()), "Could not put the child in a job object; only the direct child will be killed");
            handle.Dispose();
            return null;
        }
        return handle;
    }

    /// <summary>Creates a pipe whose child end is inheritable and whose parent end is not.</summary>
    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(bool parentWrites)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), bInheritHandle = true };
        HANDLE read, write;
        if (!PInvoke.CreatePipe(&read, &write, &sa, 0)) throw Fail("Could not create a pipe");
        var r = new SafeFileHandle(read, ownsHandle: true);
        var w = new SafeFileHandle(write, ownsHandle: true);
        var parentEnd = parentWrites ? write : read;
        if (!PInvoke.SetHandleInformation(parentEnd, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0))
        {
            r.Dispose();
            w.Dispose();
            throw Fail("Could not configure a pipe");
        }
        return (r, w);
    }

    /// <summary>"KEY=value\0...\0\0", sorted case-insensitively as Windows expects.</summary>
    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? environment)
    {
        IEnumerable<KeyValuePair<string, string>> vars = environment ?? System.Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty);
        var sb = new StringBuilder();
        foreach (var (key, value) in vars.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Length == 0 || key.Contains('=') || key.Contains('\0') || value.Contains('\0')) continue;
            sb.Append(key).Append('=').Append(value).Append('\0');
        }
        sb.Append('\0');
        if (sb.Length == 1) sb.Append('\0');
        return sb.ToString().ToCharArray();
    }

    private static ChildProcessException Fail(string what) =>
        new($"{what}.", new Win32Exception(Marshal.GetLastWin32Error()));
}
