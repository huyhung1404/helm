using Helm.Core.Modules;
using Helm.Core.Settings;

namespace Helm.Modules.AlwaysOnTop;

/// <summary>Always On Top's ids, for the code that does not know the Windows module.</summary>
public static class AlwaysOnTopIds
{
    /// <summary>The module's id: its settings file and its enabled state.</summary>
    public const string ModuleId = "always-on-top";
}

/// <summary>A window kept above the others.</summary>
public sealed record PinnedWindowInfo(nint Handle, string Title, string ProcessName);

/// <summary>
/// The tool as its page sees it (on Windows: AlwaysOnTopModule): its settings, the windows kept on top right now and
/// unpinning them. Every action is a no-op while the tool is off.
/// </summary>
public interface IAlwaysOnTop : IModule
{
    ISettingsStore<AlwaysOnTopSettings> Settings { get; }

    /// <summary>The windows kept on top right now (none while the tool is off).</summary>
    IReadOnlyList<PinnedWindowInfo> PinnedWindows { get; }

    /// <summary>Raised on a background thread whenever <see cref="PinnedWindows"/> changes.</summary>
    event EventHandler? PinnedWindowsChanged;

    void Unpin(nint hwnd);

    Task UnpinAllAsync();
}
