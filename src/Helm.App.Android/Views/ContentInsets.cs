using Avalonia;

namespace Helm.App.Android.Views;

/// <summary>
/// The padding the shell needs to stay clear of the system bars and cutout, in DIPs, measured by MainActivity from
/// Android's own window insets and the view's position (parents that already moved the view clear count there).
/// </summary>
internal static class ContentInsets
{
    public static Thickness Current { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public static event EventHandler? Changed;

    /// <returns>True when the padding changed.</returns>
    public static bool Update(Thickness value)
    {
        if (value == Current) return false;
        Current = value;
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }
}
