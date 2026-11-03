using System.Text.Json;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Core.Text;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes for AI agents over MCP: search, read, write, move to the trash (never delete for good), and link a note to a
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
        new("notes_search", "Find the user's notes. Without a query, the most recently edited ones. Returns ids, titles and a short preview; read a note with notes_read. " +
            "To find a note by what it is about, pass any: several words the note may use instead of the user's own, e.g. for \"lỗi server hôm trước\": lỗi server, server error, sập, crash, down, VPS, 500.",
            McpArgs.Schema(
                ("query", McpArgs.Text("Words to look for in titles and text, all of them (accents and case do not matter)."), false),
                ("any", McpArgs.List("Alternatives, any of which may be in the note: synonyms, Vietnamese and English, short phrases. Notes with more of them come first, the newer first among equals."), false),
                ("limit", McpArgs.Number($"How many at most (default {DefaultLimit}, at most {MaxLimit})."), false),
                ("trash", McpArgs.Flag("Search the trash instead (notes deleted in the last 30 days)."), false),
                ("untitled", McpArgs.Flag("Only notes without a title of their own (the list shows their first line)."), false)),
            (args, _) => Task.FromResult<object?>(Search(args))) { ReadOnly = true },
        new("notes_read", "The full text of a note, with the tasks and people it is linked to.",
            McpArgs.Schema(("id", McpArgs.Text("The note's id (from notes_search)."), true)),
            (args, _) => Task.FromResult<object?>(Read(McpArgs.RequiredString(args, "id")))) { ReadOnly = true },
        new("notes_create", "Write a new note. The first line of the text is its title when no title is given. A note the user " +
            "dictated (speech recognition, \"ghi chú: …\"): fix its punctuation and capitals, split it into paragraphs and give it a " +
            $"short title; keep their words. Tags in use: {TagsInUse()}.",
            McpArgs.Schema(
                ("title", McpArgs.Text("The title (optional)."), false),
                ("text", McpArgs.Text("The text of the note."), true),
                ("tags", McpArgs.List("Tags added as a last line of hashtags, without '#': idea, and the project's tag from the tags in use when one fits."), false)),
            (args, _) => Task.FromResult<object?>(Create(McpArgs.String(args, "title") ?? "", NoteTags.Append(McpArgs.RequiredString(args, "text"), McpArgs.Strings(args, "tags")))))
        {
            // Into the trash, as a note the user deletes: restorable for 30 days.
            Undo = (result, _) => { store.MoveToTrash(result.GetProperty("id").GetString()!); return Task.CompletedTask; },
        },
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
        new("notes_add_important_day",
            $"Remember a day in the note “{ImportantDays.NoteTitle}”: every year (a birthday; a giỗ, often a lunar day) or one day only " +
            "(a car inspection, an insurance or domain renewal, with its year). Helm reminds the user a few days before.",
            McpArgs.Schema(
                ("label", McpArgs.Text("What the day is, in the user's words, e.g. Sinh nhật mẹ, Giỗ ông nội, Đăng kiểm xe."), true),
                ("date", McpArgs.Text("The day: 15/03 every year; 15/03/2027 or 2027-03-15 for that day only. A lunar day: its lunar day and month."), true),
                ("lunar", McpArgs.Flag("The date is a lunar one (âm lịch), every year."), false)),
            (args, _) => Task.FromResult<object?>(AddImportantDay(args)))
        {
            Undo = (result, _) => { RemoveImportantDay(result); return Task.CompletedTask; },
        },
        new("notes_retitle", "Give a note that has no title of its own a title and a few tags (#words on a last line of its text).",
            McpArgs.Schema(
                ("id", McpArgs.Text("The note's id."), true),
                ("title", McpArgs.Text("The title, short, in the note's language."), true),
                ("tags", McpArgs.List("A few tags, one or two words each, without #."), false)),
            (args, _) => Task.FromResult<object?>(Retitle(args)))
        {
            Undo = (result, _) => { UndoRetitle(result); return Task.CompletedTask; },
        },
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
        if (McpArgs.Bool(args, "untitled") == true) notes = [.. notes.Where(n => n.Value.Title.Trim().Length == 0)];
        if (terms.Count > 0) notes = [.. notes.Where(n => TextSearch.Score(terms, n.Value.DisplayTitle, n.Value.Body) > 0)];
        var any = McpArgs.Strings(args, "any");
        var found = any.Count > 0
            ? NoteRecall.Rank(notes, any, store.Now).Take(limit)
            : terms.Count == 0
                ? notes.Take(limit)
                : notes.OrderByDescending(n => TextSearch.Score(terms, n.Value.DisplayTitle, n.Value.Body)).Take(limit);
        return found.Select(n => new
        {
            id = n.Id,
            title = n.Value.DisplayTitle,
            preview = NoteText.Preview(n.Value.Title, n.Value.Body, 200),
            edited = n.Value.EditedAt,
            pinned = n.Value.Pinned,
        }).ToList();
    }

    /// <summary>The hashtags of the user's notes, the most used first, for the model to tag a new note the same way.</summary>
    private string TagsInUse() =>
        NoteTags.InUse(store.Notes().Select(n => n.Value), 25) is { Count: > 0 } tags ? string.Join(", ", tags.Select(t => $"#{t.Tag} ({t.Notes})")) : "none yet";

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

    private object AddImportantDay(JsonElement args)
    {
        var label = McpArgs.RequiredString(args, "label").Replace('\n', ' ').Trim();
        if (label.Length == 0) throw new McpToolException("label is required.");
        var date = McpArgs.RequiredString(args, "date");
        var (day, month, year) = ImportantDays.ParseDate(date) ?? throw new McpToolException($"{date} is not a day like 15/03, 15/03/2027 or 2027-03-15.");
        var lunar = McpArgs.Bool(args, "lunar") == true;
        var line = new ImportantDay(day, month, lunar ? null : year, lunar, label).Line();
        var existing = store.Notes().FirstOrDefault(n => string.Equals(n.Value.Title.Trim(), ImportantDays.NoteTitle, StringComparison.CurrentCultureIgnoreCase));
        if (existing is null) return new { id = store.Add(ImportantDays.NoteTitle, line), line, created = true };
        var note = existing.Value;
        if (note.Body.Split('\n').Any(l => l.Trim() == line)) return new { id = existing.Id, line, created = false, already = true };
        var body = note.Body.Trim().Length == 0 ? line : note.Body.TrimEnd('\n') + "\n" + line;
        if (note.Title.Length + body.Length > NoteItem.MaxLength) throw new McpToolException($"A note holds at most {NoteItem.MaxLength} characters.");
        store.Save(existing.Id, note.Title, body, note.Rev);
        return new { id = existing.Id, line, created = false };
    }

    /// <summary>Takes the day's line out again (a note made for it goes to the trash).</summary>
    private void RemoveImportantDay(JsonElement result)
    {
        if (result.TryGetProperty("already", out var already) && already.ValueKind == JsonValueKind.True) return;
        var id = result.GetProperty("id").GetString()!;
        var line = result.GetProperty("line").GetString()!;
        if (store.Get(id) is not { Trashed: false } note) return;
        var lines = note.Body.Split('\n').ToList();
        var at = lines.FindLastIndex(l => l.Trim() == line);
        if (at < 0) return;
        lines.RemoveAt(at);
        var body = string.Join("\n", lines);
        if (result.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.True && body.Trim().Length == 0) store.MoveToTrash(id);
        else store.Save(id, note.Title, body, note.Rev);
    }

    private object Retitle(JsonElement args)
    {
        var id = McpArgs.RequiredString(args, "id");
        var note = store.Get(id) ?? throw new McpToolException($"There is no note {id}.");
        if (note.Trashed) throw new McpToolException("The note is in the trash.");
        if (note.Title.Trim().Length > 0) throw new McpToolException($"The note has a title already: {note.Title}.");
        var title = NoteText.Shorten(McpArgs.RequiredString(args, "title").Replace('\n', ' ').Trim(), 80);
        if (title.Length == 0) throw new McpToolException("title is required.");
        var tags = McpArgs.Strings(args, "tags").Select(Tag).Where(t => t.Length > 1).Distinct(StringComparer.Ordinal).Take(5).ToList();
        var tagLine = string.Join(" ", tags);
        var body = tagLine.Length == 0 ? note.Body : note.Body.TrimEnd('\n') + "\n" + tagLine;
        if (title.Length + body.Length > NoteItem.MaxLength) throw new McpToolException($"A note holds at most {NoteItem.MaxLength} characters.");
        var saved = store.Save(id, title, body, note.Rev);
        if (saved.Outcome != NoteSaveOutcome.Saved) throw new McpToolException("The note changed meanwhile; it was left as it is.");
        return new { id, title, tags = tagLine };
    }

    /// <summary>The title goes, and the tag line with it, unless the user has given the note another title since.</summary>
    private void UndoRetitle(JsonElement result)
    {
        var id = result.GetProperty("id").GetString()!;
        var title = result.GetProperty("title").GetString()!;
        var tags = result.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "";
        if (store.Get(id) is not { Trashed: false } note || note.Title != title) return;
        var lines = note.Body.Split('\n').ToList();
        if (tags.Length > 0 && lines.Count > 0 && lines[^1].Trim() == tags) lines.RemoveAt(lines.Count - 1);
        store.Save(id, "", string.Join("\n", lines), note.Rev);
    }

    /// <summary>"Công việc" → "#công-việc".</summary>
    private static string Tag(string text) =>
        "#" + string.Join("-", text.Trim().ToLowerInvariant().Split([' ', '\t', '#', ','], StringSplitOptions.RemoveEmptyEntries));

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
