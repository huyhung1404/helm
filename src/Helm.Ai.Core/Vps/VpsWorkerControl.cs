using Helm.Core.Settings;

namespace Helm.Ai.Vps;

/// <summary>
/// Runs the <see cref="VpsWorker"/> as this PC's settings say: on, with an address and a token, it serves that VPS;
/// otherwise it says what is missing. A change of address or token starts it over.
/// </summary>
public sealed class VpsWorkerControl
{
    private readonly ISettingsStore<VpsWorkerSettings> _settings;
    private readonly VpsWorkerToken _token;
    private readonly VpsWorker _worker;
    private readonly object _gate = new();
    private int _tokenVersion;
    private (string Url, int Token)? _serving;

    public VpsWorkerControl(ISettingsStore<VpsWorkerSettings> settings, VpsWorkerToken token, VpsWorker worker)
    {
        _settings = settings;
        _token = token;
        _worker = worker;
        _settings.Changed += (_, _) => Apply();
    }

    public VpsWorkerSettings Settings => _settings.Current;

    public bool HasToken => _token.Has;

    /// <summary>Saves the address, or says why it is not one (then nothing changes).</summary>
    public string? SaveUrl(string text)
    {
        if (VpsUrls.Check(text, out var problem) is not { } url) return problem;
        _settings.Update(s => s.Url = url.ToString());
        return null;
    }

    public void SaveToken(string token)
    {
        _token.Save(token);
        Interlocked.Increment(ref _tokenVersion);
        Apply();
    }

    public void SetEnabled(bool on) => _settings.Update(s => s.Enabled = on);

    /// <summary>Starts, starts over or stops the worker as the settings now say.</summary>
    public void Apply()
    {
        lock (_gate)
        {
            var settings = _settings.Current;
            Uri? url = null;
            string? problem = null;
            var why = !settings.Enabled ? "turned off"
                : settings.Url.Length == 0 ? "add your VPS's address"
                : (url = VpsUrls.Check(settings.Url, out problem)) is null ? problem
                : !_token.Has ? "save the worker token"
                : null;
            if (why is not null || url is null)
            {
                _serving = null;
                _worker.Stop(why ?? "turned off");
                return;
            }
            var serving = (settings.Url, _tokenVersion);
            if (_serving == serving) return;
            if (_token.Get() is not { } token)
            {
                _serving = null;
                _worker.Stop("the saved token cannot be read on this PC: save it again");
                return;
            }
            _serving = serving;
            _worker.Start(url, token);
        }
    }
}
