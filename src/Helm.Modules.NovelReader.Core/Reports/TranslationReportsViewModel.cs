using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Text;
using Helm.Shell.Services;

namespace Helm.Modules.NovelReader.Reports;

/// <summary>One report in the list.</summary>
public sealed class ReportRowViewModel(string id, TranslationReport report, bool? rightNow)
{
    public string Id { get; } = id;

    public TranslationReport Report { get; } = report;

    public string Chinese => Report.Chinese;

    public string Helm => Report.Helm;

    public string Correct => Report.Correct;

    public string Note => Report.Note;

    public bool HasCorrect => Report.Correct.Length > 0;

    public bool HasNote => Report.Note.Length > 0;

    public string Where => Report.ChapterTitle.Length > 0 ? $"{Report.BookTitle} · {Report.ChapterTitle}" : Report.BookTitle;

    /// <summary>Helm now shows the right translation (null: not known, the report is of another novel than the open one).</summary>
    public bool IsRightNow => rightNow == true;

    public string Suggestion => Report.Suggestion;

    public bool HasSuggestion => Report.Suggestion.Length > 0;

    /// <summary>The AI's note with the kind of error: "的 bị ngược · word-order".</summary>
    public string SuggestionNote => string.Join(" · ", new[] { Report.SuggestionNote, Report.ErrorKind }.Where(s => s.Length > 0));

    /// <summary>The suggestion is not the right translation yet, so taking it changes something.</summary>
    public bool CanUseSuggestion => HasSuggestion && Report.Suggestion != Report.Correct;
}

/// <summary>
/// The reports of wrong translations: the form to make one from the word popup, the list of the open novel's reports
/// (or all of them), the whole list as a document to copy or save, and, where an AI is on this device, its suggested
/// translations. The page view model gives the context (<see cref="ReportContext"/>); everything else lives here.
/// Used on the UI thread; store events are posted.
/// </summary>
public sealed partial class TranslationReportsViewModel : ObservableObject
{
    private readonly TranslationReportStore _store;
    private readonly IUiDispatcher _ui;
    private readonly IClipboardService _clipboard;
    private readonly Func<ReportContext?> _context;
    private readonly ITranslationAgent? _agent;
    private CancellationTokenSource? _askCancel;
    private bool _refreshQueued;

    [ObservableProperty] private bool _isPanelOpen;
    [ObservableProperty] private bool _showAllNovels;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isAsking;
    [ObservableProperty] private string _askStatus = "";

    public TranslationReportsViewModel(TranslationReportStore store, IUiDispatcher ui, IClipboardService clipboard, IDeviceInfo device,
        Func<ReportContext?> context, ITranslationAgent? agent = null)
    {
        _store = store;
        _ui = ui;
        _clipboard = clipboard;
        _context = context;
        _agent = agent;
        Editor = new ReportEditorViewModel(store, () => device.DeviceName)
        {
            CanAsk = agent is not null,
            Ask = id => AskAsync([id]),
        };
        _store.Changed += (_, _) => _ui.Post(OnStoreChanged);
    }

    /// <summary>An AI on this device suggests translations (the PC with Claude Code).</summary>
    public bool CanAsk => _agent is not null;

    /// <summary>The AI's name for buttons ("Ask Claude Code").</summary>
    public string AskLabel => _agent is null ? "" : $"Ask {_agent.Name}";

    public bool HasAskStatus => AskStatus.Length > 0;

    partial void OnIsAskingChanged(bool value) => Editor.IsAsking = value;

    partial void OnAskStatusChanged(string value)
    {
        Editor.AskStatus = value;
        OnPropertyChanged(nameof(HasAskStatus));
    }

    public ReportEditorViewModel Editor { get; }

    public ObservableCollection<ReportRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasStatus => Status is not null;

    /// <summary>Raised when the panel opens, so the page closes the other side panel.</summary>
    public event EventHandler? PanelOpened;

    partial void OnIsPanelOpenChanged(bool value)
    {
        if (!value) return;
        Status = null;
        Refresh();
        PanelOpened?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowAllNovelsChanged(bool value) => Refresh();

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Starts a report on the sentence of a clicked word; nothing happens while no novel is open.</summary>
    public void Start(ConvertedLine line, int start, int length)
    {
        if (_context() is { } context) Editor.Open(line, start, length, context);
    }

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    [RelayCommand]
    private void Edit(ReportRowViewModel? row)
    {
        if (row is not null && _store.Get(row.Id) is { } report) Editor.Edit(row.Id, report);
    }

    [RelayCommand]
    private void Delete(ReportRowViewModel? row)
    {
        if (row is not null) _store.Delete(row.Id);
    }

    /// <summary>Takes the AI's suggestion as the report's right translation.</summary>
    [RelayCommand]
    private void UseSuggestion(ReportRowViewModel? row)
    {
        if (row is not null) _store.UseSuggestion(row.Id);
    }

    /// <summary>Asks the AI about every listed report it has not suggested a translation for yet.</summary>
    [RelayCommand]
    private Task AskForRest() => AskAsync(Listed().Where(r => r.Value.Suggestion.Length == 0).Select(r => r.Id).ToList());

    [RelayCommand]
    private void StopAsking() => _askCancel?.Cancel();

    /// <summary>
    /// Has the AI suggest translations for these reports, a novel at a time; its suggestions arrive through the store
    /// while it works. Nothing happens while it is already working.
    /// </summary>
    public async Task AskAsync(IReadOnlyList<string> ids)
    {
        if (_agent is null || IsAsking) return;
        if (ids.Count == 0)
        {
            AskStatus = "Every listed report has a suggestion already.";
            return;
        }
        if (_agent.Unavailable() is { } problem)
        {
            AskStatus = problem;
            return;
        }
        var byBook = ids.Select(id => (Id: id, Report: _store.Get(id))).Where(r => r.Report is not null)
            .GroupBy(r => (r.Report!.BookId ?? "", r.Report.BookTitle)).ToList();
        IsAsking = true;
        using var cancel = new CancellationTokenSource();
        _askCancel = cancel;
        var progress = new UiProgress(_ui, text => AskStatus = text);
        try
        {
            var said = "";
            foreach (var group in byBook)
            {
                AskStatus = $"{_agent.Name} is reading {Count(group.Count())}…";
                said = await _agent.SuggestAsync(group.Key.Item1, group.Key.BookTitle, group.Select(r => r.Id).ToList(), progress, cancel.Token);
            }
            AskStatus = string.IsNullOrWhiteSpace(said) ? $"{_agent.Name} is done." : $"{_agent.Name}: {said.Trim()}";
        }
        catch (TranslationAgentException ex)
        {
            AskStatus = ex.Message;
        }
        catch (OperationCanceledException)
        {
            AskStatus = "Stopped. The suggestions made so far stay.";
        }
        finally
        {
            _askCancel = null;
            IsAsking = false;
        }
    }

    /// <summary>The listed reports as a Markdown document on the clipboard.</summary>
    [RelayCommand]
    private void CopyAll()
    {
        var reports = Listed();
        if (reports.Count == 0) return;
        _clipboard.SetText(Document(reports));
        Status = $"Copied {Count(reports.Count)} as a document.";
    }

    /// <summary>Saves the listed reports as a Markdown document (the page asks where).</summary>
    public void ExportTo(string path)
    {
        try
        {
            TextFiles.WriteAllTextAtomic(path, Document(Listed()));
            Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not save: " + ex.Message;
        }
    }

    /// <summary>Lists the reports again (the open novel changed, or the store did).</summary>
    public void Refresh()
    {
        _refreshQueued = false;
        var context = _context();
        var converter = context?.Converter;
        Rows.Clear();
        foreach (var (id, report, _) in Listed())
        {
            bool? rightNow = converter is not null && report.BookId == context!.BookId
                ? TranslationReportDocument.IsFixed(report, converter.ConvertLine(report.Chinese).Text)
                : null;
            Rows.Add(new ReportRowViewModel(id, report, rightNow));
        }
        var here = context is null ? 0 : _store.Count(context.BookId);
        var all = _store.Count();
        Summary = ShowAllNovels || context is null
            ? $"{Count(all)} in all"
            : $"{Count(here)} for this novel · {all} in all";
        OnPropertyChanged(nameof(HasRows));
    }

    private void OnStoreChanged()
    {
        // An open form shows the suggestion as soon as it arrives.
        if (Editor.IsOpen && Editor.EditingId is { } id && _store.Get(id) is { } report) Editor.Reload(report);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        // A sync brings many records at once: one refresh after them, and none while nobody looks at the list.
        if (_refreshQueued || !IsPanelOpen) return;
        _refreshQueued = true;
        _ui.Post(Refresh);
    }

    private IReadOnlyList<SyncedItem<TranslationReport>> Listed() =>
        _store.Reports(ShowAllNovels ? null : _context()?.BookId);

    private string Document(IReadOnlyList<SyncedItem<TranslationReport>> reports)
    {
        // The open novel's converter knows its names: only its reports can say what Helm shows now.
        var context = _context();
        return TranslationReportDocument.Build(reports, DateTimeOffset.Now, context is null ? null
            : report => report.BookId == context.BookId ? context.Converter.ConvertLine(report.Chinese).Text : null);
    }

    private static string Count(int n) => n == 1 ? "1 report" : $"{n} reports";

    /// <summary>Progress from the AI's thread, shown on the UI thread.</summary>
    private sealed class UiProgress(IUiDispatcher ui, Action<string> show) : IProgress<string>
    {
        public void Report(string value) => ui.Post(() => show(value));
    }
}
