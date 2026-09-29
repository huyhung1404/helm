using Helm.Core.Palette;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes in the command palette: notes whose title or text match, the most recent ones when nothing is typed, and
/// "New note". The shell navigation is resolved when a result runs (it depends on the module list, which the palette
/// module is part of).
/// </summary>
internal sealed class NotesPaletteProvider(NotesStore store, NotesViewModel viewModel, IServiceProvider services) : IPaletteProvider
{
    private const int MaxResults = 8;
    private const int Recent = 3;

    public string? ModuleId => NotesIds.ModuleId;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        var now = store.Now;
        var notes = store.Notes();
        if (query.IsEmpty)
        {
            foreach (var (id, note, _) in notes.Take(Recent))
                yield return Item(id, note, now, 0.1);
            yield break;
        }

        var newScore = Math.Max(query.Score("New note"), query.Score("Ghi chú mới"));
        if (newScore > 0) yield return new PaletteItem("New note", "Notes", PaletteKind.Action, newScore, () => Open(null));

        foreach (var (id, note, score) in notes
                     .Select(n => (n.Id, n.Value, score: query.Score(n.Value.DisplayTitle, n.Value.Body)))
                     .Where(x => x.score > 0)
                     .OrderByDescending(x => x.score)
                     .Take(MaxResults))
        {
            yield return Item(id, note, now, score);
        }
    }

    private PaletteItem Item(string id, NoteItem note, DateTimeOffset now, double score) =>
        new(note.DisplayTitle, $"Note · edited {NotesFormat.When(note.EditedAt, now)}", PaletteKind.Note, score, () => Open(id));

    private void Open(string? id)
    {
        services.GetRequiredService<IShellNavigation>().ShowPage(typeof(NotesContentPage));
        if (id is null) viewModel.NewNoteCommand.Execute(null);
        else viewModel.OpenNote(id);
    }
}
