using Avalonia;

namespace Helm.App.Android.Views;

/// <summary>
/// The part of the window's edges the Avalonia view does not cover, in DIPs (measured by MainActivity). Parents that
/// already moved the view clear of the system bars count here, so the shell must not pad for them again.
/// </summary>
internal static class ContentInsets
{
    public static Thickness Current { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public static event EventHandler? Changed;

    public static void Update(Thickness value)
    {
        if (value == Current) return;
        Current = value;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The safe area still to pad inside the view: what the system bars cover minus what parents already cleared.</summary>
    public static Thickness Remaining(Thickness safeArea) => new(
        Math.Max(0, safeArea.Left - Current.Left),
        Math.Max(0, safeArea.Top - Current.Top),
        Math.Max(0, safeArea.Right - Current.Right),
        Math.Max(0, safeArea.Bottom - Current.Bottom));
}
