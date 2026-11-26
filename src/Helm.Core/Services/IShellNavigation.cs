namespace Helm.Core.Services;

/// <summary>Lets a module bring up its own page (e.g. from a hotkey) without depending on the app.</summary>
public interface IShellNavigation
{
    /// <summary>
    /// Android: shows <paramref name="pageType"/> (an Avalonia page resolved from DI). The Windows app has no page
    /// types; it uses <see cref="ShowTool"/>.
    /// </summary>
    void ShowPage(Type pageType);

    /// <summary>
    /// Windows: shows a page by its key: a tool's module id, or "home", "general", "bug-reports". Its tab is opened if
    /// needed and put in front, and that window is shown and activated.
    /// </summary>
    void ShowTool(string key) => throw new NotSupportedException("This app navigates by page type.");
}
