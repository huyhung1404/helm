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

/// <summary>Android: hands a file to another app through the share sheet (Windows saves files with a dialog instead).</summary>
public interface IFileSharer
{
    /// <summary>Where to write a file before sharing it (a cache folder the share sheet can read).</summary>
    string ShareFolder { get; }

    void ShareFile(string path, string mimeType, string title);
}
