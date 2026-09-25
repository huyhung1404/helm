namespace Helm.Core.Services;

/// <summary>Lets a module bring up its own page (e.g. from a hotkey) without depending on Helm.App.</summary>
public interface IShellNavigation
{
    /// <summary>Shows and activates the main window on <paramref name="pageType"/>.</summary>
    void ShowPage(Type pageType);
}
