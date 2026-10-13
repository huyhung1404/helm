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
    /// <summary>Opens the navigation menu (set by the shell).</summary>
    public static Action? OpenNavigation { get; set; }
}
