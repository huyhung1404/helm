using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Ai.Vps;
using Helm.Core.Services;

namespace Helm.App.ViewModels;

/// <summary>
/// The "Your VPS" part of the Proxy tab: the address and worker token this PC serves the user's VPS with, on or off,
/// and how it stands. The token is never shown again once saved.
/// </summary>
internal sealed partial class VpsWorkerViewModel : ObservableObject
{
    private readonly VpsWorkerControl _control;
    private readonly VpsWorker _worker;
    private readonly IUiDispatcher _ui;

    [ObservableProperty] private string _url;
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _hasToken;
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _statusText = "";

    public VpsWorkerViewModel(VpsWorkerControl control, VpsWorker worker, IUiDispatcher ui)
    {
        _control = control;
        _worker = worker;
        _ui = ui;
        _url = control.Settings.Url;
        _isOn = control.Settings.Enabled;
        _hasToken = control.HasToken;
        _worker.StatusChanged += (_, _) => _ui.Post(ShowStatus);
        ShowStatus();
    }

    partial void OnIsOnChanged(bool value) => _control.SetEnabled(value);

    partial void OnUrlChanged(string value) => Problem = "";

    /// <summary>Saves the address and, when one was typed, the token.</summary>
    [RelayCommand]
    private void Save()
    {
        if (_control.SaveUrl(Url) is { } problem)
        {
            Problem = problem;
            return;
        }
        if (Token.Trim().Length > 0)
        {
            _control.SaveToken(Token);
            Token = "";
            HasToken = true;
        }
        Problem = HasToken ? "" : "Paste the worker token your VPS gave this PC.";
    }

    private void ShowStatus()
    {
        var status = _worker.Status;
        StatusText = status switch
        {
            { Connected: true } => $"Connected to {status.Host} · {status.JobsToday} {(status.JobsToday == 1 ? "job" : "jobs")} today",
            { Problem: null } => $"Connecting to {status.Host}…",
            _ => $"Not connected: {status.Problem}",
        };
    }
}
