using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;

namespace Helm.Modules.Vocab;

/// <summary>An AI agent that works on the user's words for them (on Windows: Claude Code without a window).</summary>
public interface IVocabAgent
{
    /// <summary>Why it cannot run now (not installed, not connected to Helm), or null when it can.</summary>
    string? Unavailable();

    /// <summary>Breaks down the words waiting for an analysis; returns its last answer.</summary>
    Task<string> AnalyzeAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Adds and breaks down a few useful words with this part (<paramref name="partId"/>) that the user does not have yet.</summary>
    Task<string> SuggestAsync(string partId, string form, string meaning, VocabLanguage language, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>Claude Code (or any agent) could not do the job; the message says why.</summary>
public sealed class VocabAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The agent on the page: "Analyze now", "Suggest words with this root", and (PC, when the setting is on) breaking down
/// new words by itself a moment after they arrive from any device. One job at a time. UI thread only, except the
/// timer, which posts to it.
/// </summary>
public sealed partial class VocabAgentViewModel : ObservableObject, IDisposable
{
    /// <summary>Words that arrive wait this long, so a few added in a row go in one run.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>After a failed run, it does not start by itself again for this long.</summary>
    internal static readonly TimeSpan Rest = TimeSpan.FromMinutes(10);

    private readonly IVocabAgent? _agent;
    private readonly VocabStore _store;
    private readonly VocabSettingsViewModel _settings;
    private readonly IUiDispatcher _ui;
    private readonly Timer _timer;
    private CancellationTokenSource? _running;
    private DateTimeOffset _restUntil = DateTimeOffset.MinValue;
    private bool _watching;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    public VocabAgentViewModel(IVocabAgent? agent, VocabStore store, VocabSettingsViewModel settings, IUiDispatcher ui)
    {
        _agent = agent;
        _store = store;
        _settings = settings;
        _ui = ui;
        _timer = new Timer(_ => _ui.Post(RunIfWaiting), null, Timeout.Infinite, Timeout.Infinite);
        _store.Changed += (_, _) => Nudge();
    }

    /// <summary>There is an agent on this device (the PC); on the phone the analysis comes from the PC by sync.</summary>
    public bool CanRun => _agent is not null;

    public bool HasStatus => Status.Length > 0;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Starts or stops breaking down new words by itself (the tool on or off).</summary>
    public void Watch(bool on)
    {
        _watching = on;
        if (on) Nudge();
        else _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private bool CanAnalyze() => CanRun && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => RunAsync("Analyzing", (progress, ct) => _agent!.AnalyzeAsync(progress, ct));

    /// <summary>A few more words with this part, added and broken down.</summary>
    public Task SuggestAsync(string partId, string form, string meaning, VocabLanguage language) =>
        RunAsync("Suggesting", (progress, ct) => _agent!.SuggestAsync(partId, form, meaning, language, progress, ct));

    [RelayCommand]
    private void Stop() => _running?.Cancel();

    private async Task RunAsync(string what, Func<IProgress<string>, CancellationToken, Task<string>> job)
    {
        if (_agent is null || IsBusy) return;
        if (_agent.Unavailable() is { } why)
        {
            Status = why;
            return;
        }
        IsBusy = true;
        Status = $"{what}…";
        using var running = new CancellationTokenSource();
        _running = running;
        try
        {
            var answer = await job(new Progress<string>(p => Status = p), running.Token);
            Status = answer.Trim() is { Length: > 0 } a ? a : "Done.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (VocabAgentException ex)
        {
            Status = ex.Message;
            _restUntil = _store.Now + Rest;
        }
        finally
        {
            _running = null;
            IsBusy = false;
        }
        // Words added while it worked get their turn.
        Nudge();
    }

    /// <summary>New words arrived (or the setting changed): a run in a moment, when it is on and has nothing to do.</summary>
    public void Nudge()
    {
        if (!_watching || _agent is null || !_settings.Current.AutoAnalyze || IsBusy) return;
        if (_store.Pending().Count == 0) return;
        try
        {
            _timer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RunIfWaiting()
    {
        if (!_watching || !_settings.Current.AutoAnalyze || IsBusy || _store.Now < _restUntil || _store.Pending().Count == 0) return;
        _ = AnalyzeAsync();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _running?.Cancel();
    }
}
