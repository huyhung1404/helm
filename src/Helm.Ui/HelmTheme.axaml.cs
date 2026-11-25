using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Helm.Core.Ui;

/// <summary>Helm's Avalonia look (HelmTheme.axaml): include it once per app with a StyleInclude.</summary>
public class HelmTheme : Styles
{
    public HelmTheme()
    {
        AvaloniaXamlLoader.Load(this);
        // Every TextBox gets Fluent's theme with only the template swapped (HelmTextBoxTemplate): based on Fluent's own
        // theme here because a XAML BasedOn cannot reach a theme that this dictionary replaces under the same key.
        if (Resources.TryGetResource("HelmTextBoxTemplate", null, out var mine) && mine is ControlTheme helm
            && this.OfType<FluentTheme>().FirstOrDefault() is { } fluent
            && fluent.TryGetResource(typeof(TextBox), ThemeVariant.Default, out var found) && found is ControlTheme fluentTextBox)
        {
            helm.BasedOn = fluentTextBox;
            Resources[typeof(TextBox)] = helm;
        }
    }
}
