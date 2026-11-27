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
/// (or all of them), the whole list as a document to copy or save, and the translations an AI agent suggested over MCP.
/// The page view model gives the context (<see cref="ReportContext"/>); everything else lives here.
/// Used on the UI thread; store events are posted.
/// </summary>
public sealed partial class TranslationReportsViewModel : ObservableObject
{
    private readonly TranslationReportStore _store;
    private readonly IUiDispatcher _ui;
    private readonly IClipboardService _clipboard;
    private readonly Func<ReportContext?> _context;
    private bool _refreshQueued;

    [ObservableProperty] private bool _isPanelOpen;
    [ObservableProperty] private bool _showAllNovels;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _status;

    public TranslationReportsViewModel(TranslationReportStore store, IUiDispatcher ui, IClipboardService clipboard, IDeviceInfo device,
        Func<ReportContext?> context)
    {
        _store = store;
        _ui = ui;
        _clipboard = clipboard;
        _context = context;
        Editor = new ReportEditorViewModel(store, () => device.DeviceName);
        _store.Changed += (_, _) => _ui.Post(OnStoreChanged);
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

    /// <summary>Takes the suggestion an AI agent wrote (novel_suggest_translations) as the report's right translation.</summary>
    [RelayCommand]
    private void UseSuggestion(ReportRowViewModel? row)
    {
        if (row is not null) _store.UseSuggestion(row.Id);
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
}
