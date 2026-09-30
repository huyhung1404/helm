using Helm.Core.Text;

namespace Helm.Core.Palette;

/// <summary>What a result is, which decides its icon in the palette.</summary>
public enum PaletteKind
{
    Page,
    Setting,
    Action,
    Note,
    Task,
    Debt,
    VaultItem,
    App,
    File,
    Folder,
    Window,
    WindowsSetting,
    Web,
    /// <summary>A saved video (Watch Later).</summary>
    Video,
}

/// <summary>One result of the command palette.</summary>
/// <param name="Title">The main line, e.g. a note's title.</param>
/// <param name="Subtitle">Where it is or what it does, e.g. "Notes · edited 5 min ago".</param>
/// <param name="Score">Higher first; from <see cref="TextSearch.Score"/> or a fixed value for suggestions.</param>
/// <param name="Run">Runs on the UI thread after the palette closes. Must not throw.</param>
public sealed record PaletteItem(string Title, string Subtitle, PaletteKind Kind, double Score, Action Run)
{
    /// <summary>A file whose own icon is shown instead of the kind's (e.g. an app's shortcut); null for the kind's icon.</summary>
    public string? IconFile { get; init; }

    /// <summary>
    /// Something outside Helm (a file, an app, a window, a web search). Helm's own results rank above these unless
    /// these match much better.
    /// </summary>
    public bool IsExternal { get; init; }
}

/// <summary>The text typed in the palette, and its folded words for <see cref="TextSearch.Score"/>.</summary>
public sealed class PaletteQuery
{
    public PaletteQuery(string text)
    {
        Text = text.Trim();
        Terms = TextSearch.Terms(Text);
    }

    public string Text { get; }

    public IReadOnlyList<string> Terms { get; }

    /// <summary>Nothing typed: providers may offer a few suggestions (recent notes, common actions).</summary>
    public bool IsEmpty => Terms.Count == 0;

    public double Score(string title, string? detail = null) => TextSearch.Score(Terms, title, detail);
}

/// <summary>
/// A source of command palette results. Tools register providers in DI; the palette asks the providers of the tools
/// that are on. Called on the UI thread for every change of the query, so it must be quick and must not throw.
/// </summary>
public interface IPaletteProvider
{
    /// <summary>The tool whose data this searches (asked only while it is on); null for the app itself.</summary>
    string? ModuleId { get; }

    IEnumerable<PaletteItem> Search(PaletteQuery query);
}

/// <summary>
/// A source that is too slow for every key (e.g. the Windows Search index). The palette asks it on a background
/// thread once typing pauses and adds its results when they arrive; a newer query cancels the older one.
/// </summary>
public interface ISlowPaletteProvider
{
    /// <inheritdoc cref="IPaletteProvider.ModuleId"/>
    string? ModuleId { get; }

    Task<IReadOnlyList<PaletteItem>> SearchAsync(PaletteQuery query, CancellationToken ct);
}
