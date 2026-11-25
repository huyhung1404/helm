using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Helm.Core.Ui;

/// <summary>
/// TextBox's text presenter (used by HelmTheme's TextBox template) that shows the word an input method is composing
/// like ordinary text. Avalonia underlines it, which for Vietnamese (Helm Keyboard composes every word) left almost
/// every word underlined while typing.
/// </summary>
public sealed class HelmTextPresenter : TextPresenter
{
    protected override Type StyleKeyOverride => typeof(TextPresenter);

    protected override TextLayout CreateTextLayout()
    {
        var layout = base.CreateTextLayout();
        var preedit = PreeditText;
        // A password box keeps Avalonia's masked layout.
        if (string.IsNullOrEmpty(preedit) || (PasswordChar != default && !RevealPassword)) return layout;

        // The same layout Avalonia builds (text with the composing word at the caret, same constraint) without the
        // underline over the composing word.
        var text = Text ?? "";
        var combined = text.Insert(Math.Clamp(CaretIndex, 0, text.Length), preedit);
        var plain = new TextLayout(combined, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            TextAlignment, TextWrapping, flowDirection: FlowDirection, maxWidth: layout.MaxWidth, maxHeight: layout.MaxHeight,
            lineHeight: LineHeight, letterSpacing: LetterSpacing, fontFeatures: FontFeatures);
        layout.Dispose();
        return plain;
    }
}
