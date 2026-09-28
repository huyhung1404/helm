namespace Helm.Core.Services;

public interface IProcessLauncher
{
    /// <summary>Full path of the running executable.</summary>
    string ExecutablePath { get; }

    bool IsElevated { get; }

    void OpenFolder(string path);

    void OpenUrl(string url);

    /// <summary>Starts a new instance of Helm (the caller is expected to shut down right after).</summary>
    void StartNewInstance(string? arguments = null);
}
