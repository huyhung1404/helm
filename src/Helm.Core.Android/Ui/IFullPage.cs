namespace Helm.Core.Ui;

/// <summary>
/// A page that fills the shell's page area itself, edge to edge and without the shell's scrolling: it lays out its
/// own fixed bars and scrolling parts (a conversation with its composer at the bottom, a terminal). The shell's app bar
/// is hidden too, to leave a phone screen to the page; the page offers the menu (<see cref="ShellChrome.OpenNavigation"/>).
/// </summary>
public interface IFullPage;

/// <summary>What the shell lends a <see cref="IFullPage"/> that drew its own top bar.</summary>
public static class ShellChrome
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avalonia.Controls.TopLevel, Action<Avalonia.Controls.Control>> Navigation = new();

    /// <summary>Set by each Helm window: what its menu button does (the main window's drawer, a tool window's menu).</summary>
    public static void SetNavigation(Avalonia.Controls.TopLevel window, Action<Avalonia.Controls.Control> open) => Navigation.AddOrUpdate(window, open);

    /// <summary>Opens the navigation menu of the window <paramref name="from"/> is in (from: the button pressed).</summary>
    public static void OpenNavigation(Avalonia.Controls.Control from)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(from) is { } window && Navigation.TryGetValue(window, out var open)) open(from);
    }
}
