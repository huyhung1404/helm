using Avalonia.Markup.Xaml.Styling;

namespace Helm.Modules.Vocab.Ui;

/// <summary>VocabStyles for a sheet built in code: a sheet opens outside the page, so it brings its own styles.</summary>
internal static class VocabSheetStyles
{
    private static readonly Uri Base = new("avares://Helm.Modules.Vocab.UI/");

    public static StyleInclude Create() => new(Base) { Source = new Uri(Base, "VocabStyles.axaml") };
}
