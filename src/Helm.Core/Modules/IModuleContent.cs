namespace Helm.Core.Modules;

/// <summary>
/// A tool you work in (lists, a chat, a vault), not only configure. It has two pages:
/// <list type="bullet">
/// <item>the <b>content</b> page (<see cref="ContentPageType"/>): what the tool is for. The navigation menu and the
/// Quick access tile on Home open it. No Enable card; when the tool is off it says so and links to its settings.</item>
/// <item>the <b>settings</b> page (<c>IHelmModule.SettingsPageType</c> / <c>IAndroidModule.PageType</c>): the Enable
/// card and the options. The Utilities list on Home (its chevron) and search open it.</item>
/// </list>
/// Tools without content (e.g. a hotkey that pins windows) do not implement this; every entry point opens their
/// settings page. Platform-neutral, so the Windows and Android modules implement the same interface.
/// </summary>
public interface IModuleContent
{
    /// <summary>The page type of the content page on this platform (a WPF Page or an Avalonia control), resolved from DI.</summary>
    Type ContentPageType { get; }
}
