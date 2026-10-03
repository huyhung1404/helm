using System.Text.Json;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Core.Text;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes for Claude over MCP: search, read, write, move to the trash (never delete for good), and link a note to a
/// task, a person in the debt book or a mission. Every change is a normal edit: it syncs, and a note edited meanwhile on another
/// device is kept as a copy rather than overwritten.
/// </summary>
public sealed class NotesMcpTools(NotesStore store, LinkHub? links = null) : IMcpToolProvider
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public string? ModuleId => NotesIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("notes_search", "Find the user's notes. Without a query, the most recently edited ones. Returns ids, titles and a short preview; read a note with notes_read.",
            McpArgs.Schema(
                ("query", McpArgs.Text("Words to look for in titles and text (accents and case do not matter)."), false),
                ("limit", McpArgs.Number($"How many at most (default {DefaultLimit}, at most {MaxLimit})."), false),
                ("trash", McpArgs.Flag("Search the trash instead (notes deleted in the last 30 days)."), false)),
            (args, _) => Task.FromResult<object?>(Search(args))) { ReadOnly = true },
        new("notes_read", "The full text of a note, with the tasks and people it is linked to.",
            McpArgs.Schema(("id", McpArgs.Text("The note's id (from notes_search)."), true)),
            (args, _) => Task.FromResult<object?>(Read(McpArgs.RequiredString(args, "id")))) { ReadOnly = true },
        new("notes_create", "Write a new note. The first line of the text is its title when no title is given.",
            McpArgs.Schema(
                ("title", McpArgs.Text("The title (optional)."), false),
                ("text", McpArgs.Text("The text of the note."), true)),
            (args, _) => Task.FromResult<object?>(Create(McpArgs.String(args, "title") ?? "", McpArgs.RequiredString(args, "text")))),
        new("notes_edit", "Change a note: a new title, new text (replaces all of it), or text to add at the end.",
            McpArgs.Schema(
                ("id", McpArgs.Text("The note's id."), true),
                ("title", McpArgs.Text("The new title."), false),
                ("text", McpArgs.Text("The new text; replaces the whole text."), false),
                ("append", McpArgs.Text("Text added after the current text, on a new line."), false)),
            (args, _) => Task.FromResult<object?>(Edit(args))),
        new("notes_trash", "Move a note to the trash (the user can restore it for 30 days).",
            McpArgs.Schema(("id", McpArgs.Text("The note's id."), true)),
            (args, _) => Task.FromResult<object?>(Trash(McpArgs.RequiredString(args, "id")))),
        new("notes_link", "Link a note to a task (task_id from tracker_tasks), a person in the debt book (their name) or a mission (mission_id from missions_list). The link shows on both sides.",
            McpArgs.Schema(
                ("id", McpArgs.Text("The note's id."), true),
                ("task_id", McpArgs.Text("A task's id."), false),
                ("person", McpArgs.Text("A person's name in the debt book."), false),
                ("mission_id", McpArgs.Text("A mission's id."), false),
                ("unlink", McpArgs.Flag("Remove the link instead."), false)),
            (args, _) => Task.FromResult<object?>(Link(args))),
    ];

    private object Search(JsonElement args)
    {
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, MaxLimit);
        var terms = TextSearch.Terms(McpArgs.String(args, "query"));
        var notes = McpArgs.Bool(args, "trash") == true ? store.Trash() : store.Notes();
        var found = terms.Count == 0
            ? notes.Take(limit)
            : notes.Select(n => (n, score: TextSearch.Score(terms, n.Value.DisplayTitle, n.Value.Body)))
                .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(limit).Select(x => x.n);
        return found.Select(n => new
        {
            id = n.Id,
            title = n.Value.DisplayTitle,
            preview = NoteText.Preview(n.Value.Title, n.Value.Body, 200),
            edited = n.Value.EditedAt,
            pinned = n.Value.Pinned,
        }).ToList();
    }

    private object Read(string id)
    {
        var note = store.Get(id) ?? throw new McpToolException($"There is no note {id}.");
        return new
        {
            id,
            title = note.DisplayTitle,
            text = note.Body,
            created = note.CreatedAt,
            edited = note.EditedAt,
            pinned = note.Pinned,
            trashed = note.Trashed,
            linked = links?.TargetsOf(new LinkRef(LinkKinds.Note, id)).Select(t => new { kind = t.Ref.Kind, id = t.Ref.Id, title = t.Title, about = t.Subtitle }).ToList(),
        };
    }

    private object Create(string title, string text)
    {
        var body = NoteText.Normalize(text);
        if (title.Length + body.Length > NoteItem.MaxLength) throw new McpToolException($"A note holds at most {NoteItem.MaxLength} characters.");
        return new { id = store.Add(title.Trim(), body) };
    }

    private object Edit(JsonElement args)
    {
        var id = McpArgs.RequiredString(args, "id");
        var note = store.Get(id) ?? throw new McpToolException($"There is no note {id}.");
        if (note.Trashed) throw new McpToolException("The note is in the trash; the user can restore it in Helm first.");
        var title = McpArgs.String(args, "title") ?? note.Title;
        var body = McpArgs.String(args, "text") is { } text ? NoteText.Normalize(text) : note.Body;
        if (McpArgs.String(args, "append") is { Length: > 0 } append)
            body = body.Length == 0 ? NoteText.Normalize(append) : body.TrimEnd('\n') + "\n" + NoteText.Normalize(append);
        if (title.Length + body.Length > NoteItem.MaxLength) throw new McpToolException($"A note holds at most {NoteItem.MaxLength} characters.");
        var result = store.Save(id, title.Trim(), body, note.Rev);
        return new
        {
            id = result.Id,
            outcome = result.Outcome switch
            {
                NoteSaveOutcome.Unchanged => "unchanged",
                NoteSaveOutcome.SavedAsCopy => "saved as a copy: the note changed meanwhile, both versions are kept",
                NoteSaveOutcome.Recreated => "the note was deleted meanwhile; the text was saved as a new note",
                _ => "saved",
            },
        };
    }

    private object Trash(string id)
    {
        if (store.Get(id) is null) throw new McpToolException($"There is no note {id}.");
        store.MoveToTrash(id);
        return new { id, trashed = true, restorable_days = NotesStore.TrashDays };
    }

    private object Link(JsonElement args)
    {
        if (links is null) throw new McpToolException("Links are not available.");
        var id = McpArgs.RequiredString(args, "id");
        if (store.Get(id) is null) throw new McpToolException($"There is no note {id}.");
        LinkRef target;
        if (McpArgs.String(args, "task_id") is { Length: > 0 } task)
        {
            var provider = links.Provider(LinkKinds.Task) ?? throw new McpToolException("Tracker is not available.");
            // A task that repeats is linked as its series, like the app does.
            var found = provider.Resolve(task) ?? throw new McpToolException($"There is no task {task}.");
            target = found.Ref;
        }
        else if (McpArgs.String(args, "person") is { Length: > 0 } person)
        {
            var provider = links.Provider(LinkKinds.Person) ?? throw new McpToolException("Tracker is not available.");
            target = provider.Search(person, 5).FirstOrDefault(t => string.Equals(t.Title, person.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Ref
                ?? throw new McpToolException($"There is nobody called {person} in the debt book.");
        }
        else if (McpArgs.String(args, "mission_id") is { Length: > 0 } mission)
        {
            var provider = links.Provider(LinkKinds.Mission) ?? throw new McpToolException("Missions is not available.");
            target = provider.Resolve(mission)?.Ref ?? throw new McpToolException($"There is no mission {mission}.");
        }
        else throw new McpToolException("Give task_id, person or mission_id.");
        var note = new LinkRef(LinkKinds.Note, id);
        var changed = McpArgs.Bool(args, "unlink") == true ? links.Unlink(note, target) : links.Link(note, target);
        return new { id, linked_to = new { kind = target.Kind, id = target.Id }, changed, unlinked = McpArgs.Bool(args, "unlink") == true };
    }
}
