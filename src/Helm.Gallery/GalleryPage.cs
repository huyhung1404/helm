using Avalonia.Controls;
using Helm.Gallery.Pages;

namespace Helm.Gallery;

/// <summary>
/// One Gallery entry: where it sits in the list, its title and how to build the real page over fake data. A tool's
/// page is the real view from Helm.Modules.&lt;X&gt;.UI with its real view model over in-memory stores; adding one is
/// one line here (and a reference to that .UI project).
/// </summary>
internal sealed record GalleryPage(string Group, string Name, string Title, Func<Control> Create)
{
    public string Label => $"{Group} · {Name}";

    public static IReadOnlyList<GalleryPage> All { get; } =
    [
        new("Base", "Controls", "Controls", () => new ControlsPage()),
    ];
}
