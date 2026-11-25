namespace Helm.Shell.Services;

/// <summary>
/// How far the auto-hiding menu reaches over the page while it is open (0 when it is shut or pinned). A WebView2 is a
/// native window that WPF cannot draw the menu over, so a page holding one steps aside by this much. UI thread only.
/// </summary>
public static class NavOverlay
{
    /// <summary>Width, in device-independent pixels, measured from the left edge of the page area.</summary>
    public static double Width { get; private set; }

    public static event EventHandler? Changed;

    public static void Set(double width)
    {
        if (Math.Abs(Width - width) < 0.5) return;
        Width = width;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
