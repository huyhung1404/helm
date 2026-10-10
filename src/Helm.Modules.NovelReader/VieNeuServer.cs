using System.Diagnostics;
using Helm.Core.Settings;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Runs VieNeu-TTS's OpenAI-style speech server for the voices on this PC: started with the folder's own Python
/// (<c>.venv\Scripts\python.exe -m apps.openai_speech</c>, 127.0.0.1 only, no console window) the first time a local
/// voice is needed, stopped when the tool is turned off, Helm exits, or (by default) after 10 minutes without reading
/// so the GPU memory is free again. A server already answering on the port is used as it is.
/// </summary>
public sealed class VieNeuServer : ILocalVoiceServer, IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    private readonly ISettingsStore<NovelReaderSettings> _settings;
    private readonly ILogger<VieNeuServer> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly Timer _idle;
    private readonly string _logFile;
    private Process? _process;
    private DateTime _lastUse = DateTime.UtcNow;
    private string _status = "";
    private bool _running;

    public VieNeuServer(ISettingsStoreFactory settings, ILogger<VieNeuServer> logger)
    {
        _settings = settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId);
        _logger = logger;
        _logFile = Path.Combine(settings.Paths.LogsDirectory, "vieneu.log");
        _idle = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _status = IsInstalled ? "Stopped" : "Not installed";
    }

    public Uri BaseAddress => new($"http://127.0.0.1:{_settings.Current.LocalVoicePort}/");

    public bool IsInstalled => Folder is { } folder && File.Exists(PythonOf(folder)) && File.Exists(Path.Combine(folder, "apps", "openai_speech.py"));

    public bool IsRunning => _running;

    public string Status => _status;

    public event EventHandler? StatusChanged;

    private string? Folder => _settings.Current.LocalVoiceFolder is { Length: > 0 } folder ? folder : null;

    public void Touch() => _lastUse = DateTime.UtcNow;

    public async Task<bool> EnsureRunningAsync(CancellationToken ct)
    {
        if (_running && await HealthyAsync(ct).ConfigureAwait(false)) return true;
        await _starting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await HealthyAsync(ct).ConfigureAwait(false))
            {
                SetStatus("Running", running: true);
                return true;
            }
            if (Folder is not { } folder || !IsInstalled)
            {
                SetStatus("Not installed");
                return false;
            }
            StopProcess();
            SetStatus("Starting (loading the voice model)…");
            var start = new ProcessStartInfo(PythonOf(folder), "-m apps.openai_speech")
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["HOST"] = "127.0.0.1";
            start.Environment["PORT"] = _settings.Current.LocalVoicePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["PYTHONUTF8"] = "1";
            // The voice model (about 1 GB) stays next to VieNeu, not in the user profile.
            start.Environment["HF_HOME"] = Path.Combine(folder, ".hf-cache");
            var process = Process.Start(start) ?? throw new InvalidOperationException("Python did not start.");
            _process = process;
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            var log = new StreamWriter(_logFile, append: false) { AutoFlush = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                lock (log) log.Dispose();
                if (_process == process) SetStatus($"Stopped (exit code {SafeExitCode(process)}; see {_logFile})");
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    SetStatus($"Could not start (see {_logFile})");
                    return false;
                }
                if (await HealthyAsync(ct).ConfigureAwait(false))
                {
                    Touch();
                    SetStatus("Running on this PC", running: true);
                    _logger.LogInformation("Local voice server started on {Address}", BaseAddress);
                    return true;
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            SetStatus("Did not answer in time");
            StopProcess();
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not start the local voice server");
            SetStatus("Could not start: " + ex.Message);
            return false;
        }
        finally
        {
            _starting.Release();
        }
    }

    public void Stop()
    {
        StopProcess();
        SetStatus(IsInstalled ? "Stopped" : "Not installed");
    }

    public void Dispose()
    {
        _idle.Dispose();
        StopProcess();
    }

    private void StopIfIdle()
    {
        try
        {
            if (_process is { HasExited: false } && _settings.Current.StopLocalVoiceWhenIdle && DateTime.UtcNow - _lastUse > IdleLimit)
            {
                _logger.LogInformation("Stopping the idle local voice server");
                Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Idle check failed");
        }
    }

    private void StopProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        process.Dispose();
        _running = false;
    }

    private async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(BaseAddress, "health"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void SetStatus(string status, bool running = false)
    {
        _running = running;
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string PythonOf(string folder) => Path.Combine(folder, ".venv", "Scripts", "python.exe");

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }
}
