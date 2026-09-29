using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Core.Text;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Notes;

/// <summary>One note in the list.</summary>
public sealed partial class NoteRowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _when = "";
    [ObservableProperty] private bool _pinned;
    [ObservableProperty] private bool _trashed;

    public NoteRowViewModel(string id) => Id = id;

    public string Id { get; }

    public bool HasPreview => Preview.Length > 0;

    partial void OnPreviewChanged(string value) => OnPropertyChanged(nameof(HasPreview));

    internal void Update(NoteItem note, DateTimeOffset now)
    {
        Title = note.DisplayTitle;
        Preview = NoteText.Preview(note.Title, note.Body);
        When = note.Trashed && note.TrashedAt is { } at ? $"Deleted {NotesFormat.When(at, now)}" : NotesFormat.When(note.EditedAt, now);
        Pinned = note.Pinned;
        Trashed = note.Trashed;
    }
}

/// <summary>
/// The Notes page, shared by the Windows and Android apps: the list (search, trash), the editor with autosave, and the
/// settings. All members are used on the UI thread; store changes (local or synced) are coalesced into one refresh
/// through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class NotesViewModel : ObservableObject, IDisposable
{
    /// <summary>Typing pauses this long before the note is saved.</summary>
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly NotesStore _store;
    private readonly ISettingsStore<NotesSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<NotesViewModel> _logger;
    private readonly Dictionary<string, NoteRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly Timer _autosave;
    private int _refreshQueued;
    private bool _loading;
    private bool _saving;

    // The note in the editor: its id (null for a new note not saved yet) and the revision the editor's text is based on.
    private string? _editorId;
    private string _baseRev = "";
    private bool _dirty;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showingTrash;
    [ObservableProperty] private NoteRowViewModel? _selectedNote;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editBody = "";
    [ObservableProperty] private bool _editPinned;
    [ObservableProperty] private bool _editTrashed;
    [ObservableProperty] private string _editorInfo = "";
    [ObservableProperty] private int _trashCount;
    [ObservableProperty] private int _sortOrderIndex;
    [ObservableProperty] private int _editorFontSize;
    [ObservableProperty] private bool _monospace;

    /// <summary>A problem or a notice about the last action; null when there is nothing to say.</summary>
    [ObservableProperty] private string? _message;

    public NotesViewModel(
        NotesStore store,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<NotesViewModel> logger)
    {
        _store = store;
        _settings = settings.Get<NotesSettings>(NotesIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _logger = logger;
        _autosave = new Timer(_ => _ui.Post(SaveNow), null, Timeout.Infinite, Timeout.Infinite);

        _loading = true;
        var s = _settings.Current;
        SortOrderIndex = Math.Clamp((int)s.SortOrder, 0, SortOrderNames.Count - 1);
        EditorFontSize = Math.Clamp(s.EditorFontSize, NotesSettings.MinFontSize, NotesSettings.MaxFontSize);
        Monospace = s.Monospace;
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        Refresh();
    }

    /// <summary>The notes shown: search results, or the trash.</summary>
    public ObservableCollection<NoteRowViewModel> Notes { get; } = [];

    public IReadOnlyList<string> SortOrderNames { get; } = ["Last edited", "Date created", "Title"];

    public IReadOnlyList<int> FontSizes { get; } =
        Enumerable.Range(NotesSettings.MinFontSize, NotesSettings.MaxFontSize - NotesSettings.MinFontSize + 1).ToList();

    /// <summary>Raised when a new note opens, so the view can put the cursor in it.</summary>
    public event EventHandler? EditorFocusRequested;

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasNotes => Notes.Count > 0;
    public bool IsListEmpty => Notes.Count == 0;
    public bool HasTrash => TrashCount > 0;
    public bool IsEditorClosed => !IsEditorOpen;
    public bool CanEditPin => IsEditorOpen && !EditTrashed;
    public string ListHeader => ShowingTrash ? "Trash" : "Notes";
    public string TrashButtonText => TrashCount == 0 ? "Trash" : $"Trash ({TrashCount})";
    public string EmptyListText => ShowingTrash
        ? $"The trash is empty. Deleted notes stay here for {NotesStore.TrashDays} days."
        : Search.Trim().Length > 0 ? $"No note contains “{Search.Trim()}”." : "No notes yet. Write your first one.";

    /// <summary>For the settings page: how many notes are in the trash.</summary>
    public string TrashSummary => TrashCount switch
    {
        0 => $"Nothing in the trash. Deleted notes stay there for {NotesStore.TrashDays} days, then they are deleted for good.",
        1 => $"1 note in the trash. Notes are deleted for good after {NotesStore.TrashDays} days.",
        _ => $"{TrashCount} notes in the trash. Notes are deleted for good after {NotesStore.TrashDays} days.",
    };

    // ---- List ----------------------------------------------------------------------------------------------------

    partial void OnSearchChanged(string value)
    {
        RefreshList();
        OnPropertyChanged(nameof(EmptyListText));
    }

    partial void OnShowingTrashChanged(bool value)
    {
        RefreshList();
        OnPropertyChanged(nameof(ListHeader));
        OnPropertyChanged(nameof(EmptyListText));
    }

    [RelayCommand]
    private void ShowTrash() => ShowingTrash = true;

    [RelayCommand]
    private void ShowNotes() => ShowingTrash = false;

    [RelayCommand]
    private void ToggleTrash() => ShowingTrash = !ShowingTrash;

    partial void OnSelectedNoteChanged(NoteRowViewModel? value)
    {
        if (_loading || value is null || value.Id == _editorId) return;
        Open(value.Id);
    }

    /// <summary>A tap on a row (Android lists rows as buttons, not as a selectable list).</summary>
    [RelayCommand]
    private void OpenRow(NoteRowViewModel? row)
    {
        if (row is not null) Open(row.Id);
    }

    /// <summary>Opens a note by id (e.g. from the command palette): leaves the trash or a search that hides it.</summary>
    public void OpenNote(string id)
    {
        if (_store.Get(id) is not { } note) return;
        _loading = true;
        try
        {
            if (Search.Length > 0) Search = "";
            if (ShowingTrash != note.Trashed) ShowingTrash = note.Trashed;
        }
        finally
        {
            _loading = false;
        }
        RefreshList();
        Open(id);
    }

    /// <summary>Windows: opens the note that was open last time (the phone starts on the list).</summary>
    public void OpenLastNote()
    {
        if (IsEditorOpen || _settings.Current.SelectedNoteId is not { } id || _store.Get(id) is not { Trashed: false }) return;
        Open(id);
    }

    [RelayCommand]
    private void NewNote()
    {
        Flush();
        _loading = true;
        try
        {
            if (ShowingTrash) ShowingTrash = false;
            SelectedNote = null;
            _editorId = null;
            _baseRev = "";
            _dirty = false;
            EditTitle = "";
            EditBody = "";
            EditPinned = false;
            EditTrashed = false;
            EditorInfo = "New note";
            IsEditorOpen = true;
        }
        finally
        {
            _loading = false;
        }
        EditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Android: back from the editor to the list (the note is saved first).</summary>
    [RelayCommand]
    public void CloseEditor()
    {
        Flush();
        _loading = true;
        try
        {
            SelectedNote = null;
            _editorId = null;
            IsEditorOpen = false;
            EditTitle = "";
            EditBody = "";
        }
        finally
        {
            _loading = false;
        }
        _settings.Update(s => s.SelectedNoteId = null);
    }

    // ---- Editor --------------------------------------------------------------------------------------------------

    partial void OnEditTitleChanged(string value) => OnTyped();

    partial void OnEditBodyChanged(string value) => OnTyped();

    private void OnTyped()
    {
        if (_loading) return;
        _dirty = true;
        _autosave.Change(AutosaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Saves what is typed now (leaving the page, closing the app, opening another note).</summary>
    public void Flush()
    {
        _autosave.Change(Timeout.Infinite, Timeout.Infinite);
        SaveNow();
    }

    private void SaveNow()
    {
        if (!_dirty || !IsEditorOpen) return;
        _saving = true;
        try
        {
            if (_editorId is null)
            {
                if (EditTitle.Trim().Length == 0 && EditBody.Trim().Length == 0) return;
                var id = _store.Add(EditTitle, EditBody);
                _editorId = id;
                _baseRev = _store.Get(id)?.Rev ?? "";
                _dirty = false;
                RememberSelection(id);
            }
            else
            {
                var result = _store.Save(_editorId, EditTitle, EditBody, _baseRev);
                _dirty = false;
                switch (result.Outcome)
                {
                    case NoteSaveOutcome.SavedAsCopy when result.CopyId is { } copy:
                        // The other device's version stays in the note; the editor carries on in the copy with its text.
                        _editorId = copy;
                        _baseRev = _store.Get(copy)?.Rev ?? "";
                        RememberSelection(copy);
                        Message = $"This note changed on another device while you were typing. Your text was saved as a separate note, “{_store.Get(copy)?.DisplayTitle}”.";
                        break;
                    case NoteSaveOutcome.Recreated:
                        _editorId = result.Id;
                        _baseRev = result.Rev;
                        RememberSelection(result.Id);
                        Message = "This note was deleted on another device. Your text was saved as a new note.";
                        break;
                    default:
                        _baseRev = result.Rev;
                        break;
                }
            }
            RefreshList();
            SelectEditorRow();
            UpdateEditorInfo();
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            // Store writes hit SQLite; a failure must never take the app down, and the text stays in the editor.
            _logger.LogError(ex, "Saving a note failed");
            Message = $"Could not save the note: {ex.Message}. Your text is still here; it is saved again when you type.";
        }
        finally
        {
            _saving = false;
        }
    }

    [RelayCommand]
    private void TogglePin()
    {
        Flush();
        if (_editorId is null) return;
        Try(() => _store.SetPinned(_editorId, !EditPinned));
    }

    [RelayCommand]
    private void Trash()
    {
        Flush();
        if (_editorId is not { } id)
        {
            // A new note that was never saved: just close it.
            CloseEditorKeepingList();
            return;
        }
        if (!Try(() => _store.MoveToTrash(id))) return;
        CloseEditorKeepingList();
        Message = "Moved to the trash.";
    }

    [RelayCommand]
    private void Restore()
    {
        if (_editorId is not { } id) return;
        if (!Try(() => _store.Restore(id))) return;
        OpenNote(id);
        Message = "Restored.";
    }

    [RelayCommand]
    private async Task DeleteForeverAsync()
    {
        if (_editorId is not { } id || _store.Get(id) is not { Trashed: true } note) return;
        var ok = await _dialogs.ConfirmAsync($"Delete “{note.DisplayTitle}” for good?",
            "It is removed from all your devices and cannot be restored.", "Delete").ConfigureAwait(true);
        if (!ok) return;
        if (Try(() => _store.DeleteForever(id))) CloseEditorKeepingList();
    }

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        var count = _store.Trash().Count;
        if (count == 0) return;
        var ok = await _dialogs.ConfirmAsync("Empty the trash?",
            count == 1 ? "1 note is removed from all your devices and cannot be restored." : $"{count} notes are removed from all your devices and cannot be restored.",
            "Empty trash").ConfigureAwait(true);
        if (!ok) return;
        if (_editorId is { } id && _store.Get(id) is { Trashed: true }) CloseEditorKeepingList();
        Try(() => _store.EmptyTrash());
    }

    [RelayCommand]
    private void CopyNote()
    {
        if (!IsEditorOpen) return;
        var title = EditTitle.Trim();
        _clipboard.SetText(title.Length > 0 ? $"{title}\n\n{EditBody}" : EditBody);
        Message = "Note copied.";
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditorClosed));
        OnPropertyChanged(nameof(CanEditPin));
    }

    partial void OnEditTrashedChanged(bool value) => OnPropertyChanged(nameof(CanEditPin));

    partial void OnTrashCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasTrash));
        OnPropertyChanged(nameof(TrashButtonText));
        OnPropertyChanged(nameof(TrashSummary));
    }

    // ---- Settings ------------------------------------------------------------------------------------------------

    partial void OnSortOrderIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        _settings.Update(s => s.SortOrder = (NotesSortOrder)Math.Clamp(value, 0, SortOrderNames.Count - 1));
        RefreshList();
    }

    partial void OnEditorFontSizeChanged(int value)
    {
        if (!_loading && value > 0) _settings.Update(s => s.EditorFontSize = Math.Clamp(value, NotesSettings.MinFontSize, NotesSettings.MaxFontSize));
    }

    partial void OnMonospaceChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.Monospace = value);
    }

    // ---- Export --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes every note (not the trash) into <paramref name="folder"/> as a Markdown file named after its title.
    /// Existing files are never overwritten: a number is added to the name instead.
    /// </summary>
    /// <returns>How many files were written.</returns>
    public int ExportMarkdown(string folder)
    {
        Flush();
        Directory.CreateDirectory(folder);
        var count = 0;
        foreach (var (_, note, _) in _store.Notes(NotesSortOrder.Title))
        {
            var name = NoteText.FileName(note.DisplayTitle);
            var path = Path.Combine(folder, name + ".md");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}).md");
            var text = note.Title.Length > 0 ? $"# {note.Title}\n\n{note.Body}\n" : note.Body + "\n";
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            count++;
        }
        return count;
    }

    /// <summary>For pages that pick the folder themselves (a folder dialog on Windows).</summary>
    public void ReportExport(int count, string folder) =>
        Message = count == 1 ? $"1 note saved to {folder}." : $"{count} notes saved to {folder}.";

    public void ReportExportFailure(Exception ex)
    {
        _logger.LogWarning(ex, "Notes export failed");
        Message = $"Could not save the notes: {ex.Message}";
    }

    public void Dispose() => _autosave.Dispose();

    // ---- Refresh -------------------------------------------------------------------------------------------------

    /// <summary>Coalesces bursts of store changes (a sync can touch hundreds of records) into one UI refresh.</summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        try
        {
            // Notes more than 30 days in the trash go for good (a no-op most of the time).
            _store.PurgeTrash();
            RefreshList();
            if (!_saving) ReloadEditorIfChangedElsewhere();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notes refresh failed");
            Message = $"Could not load the notes: {ex.Message}";
        }
    }

    private void RefreshList()
    {
        var now = _store.Now;
        var source = ShowingTrash ? _store.Trash() : _store.Notes((NotesSortOrder)Math.Clamp(SortOrderIndex, 0, SortOrderNames.Count - 1));
        var terms = TextSearch.Terms(Search);
        var shown = terms.Count == 0 ? source : source.Where(n => TextSearch.Score(terms, n.Value.DisplayTitle, n.Value.Body) > 0).ToList();

        var live = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new List<NoteRowViewModel>(shown.Count);
        foreach (var (id, note, _) in shown)
        {
            live.Add(id);
            if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new NoteRowViewModel(id);
            row.Update(note, now);
            wanted.Add(row);
        }
        foreach (var stale in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(stale);

        // Removing the selected row from the list clears the list's selection: that must not close the editor.
        var loading = _loading;
        _loading = true;
        try
        {
            Reconcile(Notes, wanted);
            SelectEditorRow();
        }
        finally
        {
            _loading = loading;
        }
        TrashCount = _store.Trash().Count;
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyListText));
    }

    /// <summary>
    /// A synced change to the open note: take it into the editor unless something typed is not saved yet (the next
    /// save then keeps both versions).
    /// </summary>
    private void ReloadEditorIfChangedElsewhere()
    {
        if (!IsEditorOpen || _editorId is not { } id) return;
        var note = _store.Get(id);
        if (note is null)
        {
            if (!_dirty)
            {
                CloseEditorKeepingList();
                Message = "This note was deleted on another device.";
            }
            return;
        }
        _loading = true;
        try
        {
            EditPinned = note.Pinned;
            EditTrashed = note.Trashed;
            if (note.Rev != _baseRev && !_dirty)
            {
                EditTitle = note.Title;
                EditBody = note.Body;
                _baseRev = note.Rev;
            }
        }
        finally
        {
            _loading = false;
        }
        UpdateEditorInfo();
    }

    private void Open(string id)
    {
        Flush();
        if (_store.Get(id) is not { } note) return;
        _loading = true;
        try
        {
            _editorId = id;
            _baseRev = note.Rev;
            _dirty = false;
            EditTitle = note.Title;
            EditBody = note.Body;
            EditPinned = note.Pinned;
            EditTrashed = note.Trashed;
            IsEditorOpen = true;
            SelectEditorRow();
        }
        finally
        {
            _loading = false;
        }
        UpdateEditorInfo();
        if (!note.Trashed) RememberSelection(id);
    }

    private void CloseEditorKeepingList()
    {
        _loading = true;
        try
        {
            _editorId = null;
            _dirty = false;
            SelectedNote = null;
            IsEditorOpen = false;
            EditTitle = "";
            EditBody = "";
        }
        finally
        {
            _loading = false;
        }
        _settings.Update(s => s.SelectedNoteId = null);
        RefreshList();
    }

    private void SelectEditorRow()
    {
        var row = _editorId is { } id ? _rows.GetValueOrDefault(id) : null;
        if (row is not null && !Notes.Contains(row)) row = null;
        if (ReferenceEquals(row, SelectedNote)) return;
        var loading = _loading;
        _loading = true;
        SelectedNote = row;
        _loading = loading;
    }

    private void UpdateEditorInfo()
    {
        if (_editorId is not { } id || _store.Get(id) is not { } note)
        {
            EditorInfo = IsEditorOpen ? "New note" : "";
            return;
        }
        var words = NotesFormat.Words(NoteText.Words(EditTitle) + NoteText.Words(EditBody));
        EditorInfo = note.Trashed
            ? $"In the trash · {words}"
            : $"Edited {NotesFormat.When(note.EditedAt, _store.Now)} · {words}";
    }

    private void RememberSelection(string id)
    {
        if (_settings.Current.SelectedNoteId != id) _settings.Update(s => s.SelectedNoteId = id);
    }

    private bool Try(Func<bool> action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notes action failed");
            Message = $"That did not work: {ex.Message}";
            return false;
        }
    }

    private bool Try(Func<int> action) => Try(() => { action(); return true; });

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="wanted"/> with minimal moves, keeping instances.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], wanted[i])) { existing = j; break; }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, wanted[i]);
        }
    }
}
