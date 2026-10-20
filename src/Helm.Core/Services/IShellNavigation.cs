namespace Helm.Core.Services;

/// <summary>Lets a module bring up its own page (e.g. from a hotkey) without depending on Helm.App.</summary>
public interface IShellNavigation
{
    /// <summary>Shows <paramref name="pageType"/>: its tab (opened if needed) in front, and that window activated.</summary>
    void ShowPage(Type pageType);
}
