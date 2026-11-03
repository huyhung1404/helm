using Avalonia;
using Avalonia.Controls;

namespace Helm.Modules.Vocab;

/// <summary>
/// A collapsible section of a card or a word's page (How it's built, Examples, Goes with…): its title is a full-width
/// row that opens and closes it (the template is in VocabStyles.axaml). Classes <c>py</c> (purple), <c>memo</c> (blue)
/// and <c>warn</c> (amber) colour the title. A section closed by the user stays closed for every word until Helm restarts, so the next card looks the same.
/// </summary>
public sealed class VocabSection : ContentControl
{
    // Sections the user closed, by key (process-wide: pages are made anew each time the tool opens).
    private static readonly HashSet<string> Closed = new(StringComparer.Ordinal);

    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<VocabSection, string>(nameof(Title), "");

    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<VocabSection, bool>(nameof(IsOpen), true);

    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<VocabSection, string>(nameof(Key), "");

    static VocabSection()
    {
        KeyProperty.Changed.AddClassHandler<VocabSection>((section, _) => section.IsOpen = !Closed.Contains(section.Key));
        IsOpenProperty.Changed.AddClassHandler<VocabSection>((section, e) =>
        {
            if (section.Key.Length == 0) return;
            if (e.NewValue is true) Closed.Remove(section.Key);
            else Closed.Add(section.Key);
        });
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Names the section where it is (learn.tree, word.examples…) so its open or closed state is remembered.</summary>
    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }
}
