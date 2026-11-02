using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;

namespace Helm.Modules.Vocab;

/// <summary>What a line of the agent's log is: a step it takes, a result, Claude's own words, a problem, the start or the end.</summary>
public enum VocabLogKind
{
    Step,
    Result,
    Note,
    Problem,
    Start,
    End,
}

/// <summary>One line of the agent's log, with the local time it came.</summary>
public sealed record VocabAgentLogLine(DateTimeOffset At, string Text, VocabLogKind Kind)
{
    public string Time => At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public bool IsResult => Kind == VocabLogKind.Result;

    public bool IsNote => Kind == VocabLogKind.Note;

    public bool IsProblem => Kind == VocabLogKind.Problem;

    /// <summary>The run's first and last lines stand out.</summary>
    public bool IsEdge => Kind is VocabLogKind.Start or VocabLogKind.End;

    /// <summary>What a progress line is, from how the narrator writes it: ✓ a result, ✗ a problem, quoted Claude's words.</summary>
    public static VocabLogKind KindOf(string text) => text switch
    {
        ['✓', ..] => VocabLogKind.Result,
        ['✗', ..] => VocabLogKind.Problem,
        ['“', ..] => VocabLogKind.Note,
        _ => VocabLogKind.Step,
    };
}

/// <summary>An AI agent that works on the user's words for them (on Windows: Claude Code without a window).</summary>
public interface IVocabAgent
{
    /// <summary>Why it cannot run now (not installed, not connected to Helm), or null when it can.</summary>
    string? Unavailable();

    /// <summary>
    /// Breaks down the words waiting for an analysis, then helps with the words the user keeps forgetting and fills in
    /// missing details; returns its last answer. <paramref name="model"/>: the Claude model (null: the user's default).
    /// </summary>
    Task<string> AnalyzeAsync(string? model, IProgress<string>? progress, CancellationToken ct);

    /// <summary>Adds and breaks down a few useful words with this part (<paramref name="partId"/>) that the user does not have yet.</summary>
    Task<string> SuggestAsync(string partId, string form, string meaning, VocabLanguage language, string? model, IProgress<string>? progress, CancellationToken ct);
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

    /// <summary>The log keeps this many lines of a run (a long run drops its oldest steps).</summary>
    internal const int MaxLogLines = 400;

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

    /// <summary>How many tool calls the run in progress (or the last one) made.</summary>
    [ObservableProperty] private int _steps;

    public VocabAgentViewModel(IVocabAgent? agent, VocabStore store, VocabQuiz quiz, VocabSettingsViewModel settings, IUiDispatcher ui)
    {
        _agent = agent;
        _store = store;
        _quiz = quiz;
        _settings = settings;
        _ui = ui;
        _timer = new Timer(_ => _ui.Post(RunIfWaiting), null, Timeout.Infinite, Timeout.Infinite);
        Log.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLog));
        _store.Changed += (_, _) =>
        {
            // The store may raise this off the UI thread.
            _ui.Post(() => OnPropertyChanged(nameof(ExternalBusy)));
            Nudge();
        };
        _store.ReviewsChanged += (_, _) => Nudge();
    }

    /// <summary>There is an agent on this device (the PC); on the phone the analysis comes from the PC by sync.</summary>
    public bool CanRun => _agent is not null;

    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// What the agent did in its run in progress, or in its last one: each step with what it worked on, each result,
    /// what Claude said, and how it ended (the Analysis dialog's Activity). Kept until the next run starts.
    /// </summary>
    public ObservableCollection<VocabAgentLogLine> Log { get; } = [];

    public bool HasLog => Log.Count > 0;

    /// <summary>The log as text, for Copy log.</summary>
    public string LogText => string.Join(Environment.NewLine, Log.Select(l => $"{l.Time}  {l.Text}"));

    /// <summary>An AI agent over MCP (not Helm's own) is working on words now; Helm's agent waits for it.</summary>
    public bool ExternalBusy => !IsBusy && (_store.HasClaimedWork() || _store.HasClaimedSource());

    /// <summary>The chip's line when <see cref="ExternalBusy"/>.</summary>
    public string ExternalStatus => "An AI agent is working on your words…";

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ExternalBusy));

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
    private Task AnalyzeAsync() => RunAsync("Analyzing", (progress, ct) => _agent!.AnalyzeAsync(Model, progress, ct));

    /// <summary>A few more words with this part, added and broken down.</summary>
    public Task SuggestAsync(string partId, string form, string meaning, VocabLanguage language) =>
        RunAsync("Suggesting", (progress, ct) => _agent!.SuggestAsync(partId, form, meaning, language, Model, progress, ct));

    /// <summary>The model the settings pick for the agent; null leaves it to Claude Code.</summary>
    private string? Model => _settings.Current.AnalysisModel is { Length: > 0 } model ? model : null;

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
        Steps = 0;
        Log.Clear();
        var started = _store.Now;
        var model = VocabSettingsViewModel.AnalysisModels.FirstOrDefault(m => m.Value == (Model ?? ""))?.Label ?? Model ?? "Claude Code's default";
        AddLog($"{what} with {model} · {PendingText()}", VocabLogKind.Start);
        using var running = new CancellationTokenSource();
        _running = running;
        try
        {
            var answer = await job(new UiProgress(_ui, OnProgress), running.Token);
            Status = answer.Trim() is { Length: > 0 } a ? a : "Done.";
            _leftBehind = Work();
            AddLog($"Done in {Took(started)} · {Steps} steps. {Status}", VocabLogKind.End);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
            AddLog($"Stopped after {Took(started)} · {Steps} steps", VocabLogKind.End);
        }
        catch (VocabAgentException ex)
        {
            Status = ex.Message;
            _restUntil = _store.Now + Rest;
            AddLog($"Stopped with a problem after {Took(started)}: {ex.Message}", VocabLogKind.Problem);
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
        try
        {
            if (Work() is not { } work || work == _leftBehind)
            {
                // Words an MCP agent took come back to Helm's agent if it never finishes them.
                if (_store.HasClaimedWork() || _store.HasClaimedSource()) _timer.Change(VocabStore.ClaimFor + Settle, Timeout.InfiniteTimeSpan);
                return;
            }
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

    /// <summary>The agent's steps, reported from its own thread, handed to the UI thread in the order they came.</summary>
    private sealed class UiProgress(IUiDispatcher ui, Action<string> report) : IProgress<string>
    {
        public void Report(string value) => ui.Post(() => report(value));
    }

    /// <summary>A step told by the agent: the line under the chip, and a line in the log.</summary>
    private void OnProgress(string line)
    {
        var kind = VocabAgentLogLine.KindOf(line);
        if (kind == VocabLogKind.Step) Steps++;
        Status = line;
        AddLog(line, kind);
    }

    private void AddLog(string text, VocabLogKind kind)
    {
        Log.Add(new VocabAgentLogLine(_store.Now, text, kind));
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    /// <summary>"3 words waiting" for the log's first line.</summary>
    private string PendingText() => _store.Pending().Count switch
    {
        0 => "no new words waiting",
        1 => "1 word waiting",
        var n => $"{n} words waiting",
    };

    /// <summary>"42 s", "2 min 05 s".</summary>
    private string Took(DateTimeOffset started)
    {
        var took = _store.Now - started;
        return took.TotalMinutes < 1 ? $"{Math.Max(0, (int)took.TotalSeconds)} s" : $"{(int)took.TotalMinutes} min {took.Seconds:00} s";
    }

    /// <summary>What there is to do, as one key (the words' ids by kind of work); null when nothing.</summary>
    internal string? Work()
    {
        var pending = _store.PendingUnclaimed().Select(w => w.Id);
        var hard = _quiz.HardWords().Select(w => w.Id).Concat(_quiz.SlippingWords().Select(w => "py:" + w.Id));
        var lacking = _store.Words().Where(w => w.Value.LacksDetails && !w.Value.Hidden).Select(w => w.Id);
        var texts = _store.WaitingSourcesUnclaimed().Select(s => s.Id);
        var key = string.Join("|", string.Join(",", pending), string.Join(",", hard.Order(StringComparer.Ordinal)), string.Join(",", lacking), string.Join(",", texts));
        return key == "|||" ? null : key;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _running?.Cancel();
    }
}
