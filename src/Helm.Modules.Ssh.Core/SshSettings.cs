using Helm.Core.Settings;

namespace Helm.Modules.Ssh;

/// <summary>
/// The servers, the server keys this device accepted and display options. Stored in settings/ssh.json on this device
/// only; nothing here is secret (passwords are never kept, and this device's key lives in <see cref="SshDeviceKey"/>).
/// </summary>
public sealed class SshSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public const int MinFontSize = 10;
    public const int MaxFontSize = 24;

    public int Version { get; set; }

    public List<SshHost> Hosts { get; set; } = [];

    public List<SshKnownHost> KnownHosts { get; set; } = [];

    /// <summary>The server the page opens on.</summary>
    public string? SelectedHostId { get; set; }

    /// <summary>Terminal text size in points.</summary>
    public int FontSize { get; set; } = 14;
}
