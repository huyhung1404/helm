using Helm.Core.Modules;
using Helm.Core.Settings;

namespace Helm.Modules.Keyboard;

public enum KeyboardInstallState
{
    /// <summary>This build of Helm has no input method next to it (a developer build).</summary>
    NotBundled,
    NotInstalled,
    /// <summary>Installed, but an older version than the one next to Helm.</summary>
    Outdated,
    Installed,
}

/// <param name="OldVersionsInUse">Folders of older versions still loaded by running apps (deleted once they close).</param>
public sealed record KeyboardInstallStatus(KeyboardInstallState State, int OldVersionsInUse);

/// <summary>
/// Helm Keyboard as its page sees it (on Windows: KeyboardModule): the settings, the keyboard each app keeps, whether the
/// input method is installed, installing and removing it.
/// </summary>
public interface IKeyboard : IModule
{
    ISettingsStore<KeyboardSettings> Settings { get; }

    KeyboardApps Apps { get; }

    /// <summary>A Chinese keyboard (Sogou, Microsoft Pinyin) is in the system's keyboard list.</summary>
    bool HasChinese { get; }

    KeyboardInstallStatus InstallStatus();

    /// <summary>Installs (or updates) the input method; false with the reason in StatusMessage.</summary>
    Task<bool> InstallAsync();

    Task<bool> UninstallAsync();

    /// <summary>Raised (on any thread) after the input method was installed, updated or removed.</summary>
    event EventHandler? InstallChanged;
}
