using Avalonia.Controls;
using Avalonia.Rendering;

namespace Helm.Core.Ui;

/// <summary>
/// "Show what is redrawn": Avalonia's own overlays on a Helm window, to check that motion stays cheap. Each frame flashes
/// the areas drawn again (a spinning glyph: just the glyph; never the whole page), with the frames per second and graphs
/// of the time spent laying out and drawing. For this window and this run only. PC: Ctrl+Alt+Shift+R; every platform:
/// General › Help &amp; troubleshooting.
/// </summary>
public static class RenderDebug
{
    private const RendererDebugOverlays All =
        RendererDebugOverlays.Fps | RendererDebugOverlays.DirtyRects | RendererDebugOverlays.LayoutTimeGraph | RendererDebugOverlays.RenderTimeGraph;

    public static bool IsOn(TopLevel? window) => window is not null && window.RendererDiagnostics.DebugOverlays != RendererDebugOverlays.None;

    public static void Set(TopLevel? window, bool on)
    {
        if (window is not null) window.RendererDiagnostics.DebugOverlays = on ? All : RendererDebugOverlays.None;
    }

    public static void Toggle(TopLevel? window) => Set(window, !IsOn(window));
}
