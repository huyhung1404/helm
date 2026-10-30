using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;

namespace Helm.Modules.Vocab;

/// <summary>An AI agent that works on the user's words for them (on Windows: Claude Code without a window).</summary>
public interface IVocabAgent
{
    /// <summary>Why it cannot run now (not installed, not connected to Helm), or null when it can.</summary>
    string? Unavailable();

    /// <summary>
    /// Breaks down the words waiting for an analysis, then helps with the words the user keeps forgetting and fills in
    /// missing details; returns its last answer.
    /// </summary>
    Task<string> AnalyzeAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Adds and breaks down a few useful words with this part (<paramref name="partId"/>) that the user does not have yet.</summary>
    Task<string> SuggestAsync(string partId, string form, string meaning, VocabLanguage language, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>Claude Code (or any agent) could not do the job; the message says why.</summary>
public sealed class VocabAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The agent on the page: "Analyze now", "Suggest words with this root", and (PC, when the setting is on) working by
/// itself a moment after there is something to do: new words from any device, a word forgotten again and again, words
/// that lack details Helm asks for now. One job at a time; when a run leaves the same work behind (the agent could not
/// do it), it does not start by itself again until something changes. UI thread only, except the timer, which posts to it.
/// </summary>
public sealed partial class VocabAgentViewModel : ObservableObject, IDisposable
{
    /// <summary>Words that arrive wait this long, so a few added in a row go in one run.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>After a failed run, it does not start by itself again for this long.</summary>
    internal static readonly TimeSpan Rest = TimeSpan.FromMinutes(10);

    private readonly IVocabAgent? _agent;
    private readonly VocabStore _store;
    private readonly VocabQuiz _quiz;
    private readonly VocabSettingsViewModel _settings;
    private readonly IUiDispatcher _ui;
    private readonly Timer _timer;
    private CancellationTokenSource? _running;
    private DateTimeOffset _restUntil = DateTimeOffset.MinValue;
    private bool _watching;
    // The work a finished run left behind: not started again by itself while nothing changes.
    private string? _leftBehind;

    /// <summary>For tests: the work the last finished run left behind.</summary>
    internal string? LeftBehind => _leftBehind;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    public VocabAgentViewModel(IVocabAgent? agent, VocabStore store, VocabQuiz quiz, VocabSettingsViewModel settings, IUiDispatcher ui)
    {
        _agent = agent;
        _store = store;
        _quiz = quiz;
        _settings = settings;
        _ui = ui;
        _timer = new Timer(_ => _ui.Post(RunIfWaiting), null, Timeout.Infinite, Timeout.Infinite);
        _store.Changed += (_, _) => Nudge();
        _store.ReviewsChanged += (_, _) => Nudge();
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
            _leftBehind = Work();
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
        if (Work() is not { } work || work == _leftBehind) return;
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
        if (!_watching || !_settings.Current.AutoAnalyze || IsBusy || _store.Now < _restUntil) return;
        if (Work() is not { } work || work == _leftBehind) return;
        _ = AnalyzeAsync();
    }

    /// <summary>What there is to do, as one key (the words' ids by kind of work); null when nothing.</summary>
    internal string? Work()
    {
        var pending = _store.Pending().Select(w => w.Id);
        var hard = _quiz.HardWords().Select(w => w.Id);
        var lacking = _store.Words().Where(w => w.Value.LacksDetails && !w.Value.Hidden).Select(w => w.Id);
        var key = string.Join("|", string.Join(",", pending), string.Join(",", hard.Order(StringComparer.Ordinal)), string.Join(",", lacking));
        return key == "||" ? null : key;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _running?.Cancel();
    }
}
