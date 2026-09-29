using Helm.Core.Links;
using Helm.Core.Text;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes as link targets: a task or a person in the debt book can have notes linked to it, and "New note" there
/// creates one titled after it. A note in the trash is hidden from its links until it is restored.
/// </summary>
/// <param name="open">Shows the note in the platform's Notes page.</param>
public sealed class NoteLinkProvider : ILinkProvider
{
    private readonly NotesStore _store;
    private readonly Action<string> _open;

    public NoteLinkProvider(NotesStore store, Action<string> open)
    {
        _store = store;
        _open = open;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Kind => LinkKinds.Note;

    public string KindName => "Note";

    public event EventHandler? Changed;

    public LinkTarget? Resolve(string id) => _store.Get(id) is { Trashed: false } note ? Target(id, note) : null;

    public IEnumerable<LinkTarget> Search(string text, int max)
    {
        var terms = TextSearch.Terms(text);
        var notes = _store.Notes();
        if (terms.Count == 0) return notes.Take(max).Select(n => Target(n.Id, n.Value)).ToList();
        return notes
            .Select(n => (n.Id, n.Value, Score: TextSearch.Score(terms, n.Value.DisplayTitle, n.Value.Body)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => Target(x.Id, x.Value))
            .ToList();
    }

    public void Open(string id) => _open(id);

    public string? Create(string title) => _store.Add(title, "");

    private LinkTarget Target(string id, NoteItem note) =>
        new(new LinkRef(Kind, id), note.DisplayTitle, "Note · edited " + NotesFormat.When(note.EditedAt, _store.Now));
}
