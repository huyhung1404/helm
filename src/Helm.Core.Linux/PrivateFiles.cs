using Microsoft.Extensions.Logging;

namespace Helm.Core.Linux;

/// <summary>
/// Files and folders only this user may read: folders 0700, files 0600 (from the moment they exist, not chmod after).
/// On Windows, where helm only runs for tests and development, the modes are left to the folder's ACL.
/// </summary>
public static class PrivateFiles
{
    public const UnixFileMode PrivateFolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates the folder (and its parents) as 0700; one that exists keeps its mode.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, PrivateFolderMode);
    }

    /// <summary>
    /// Writes a new 0600 file, whole or not at all (a temporary file moved in place); false when the file already
    /// exists (it is left as it is).
    /// </summary>
    public static bool WriteNew(string path, byte[] contents)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            return false;
        }
    }

    /// <summary>A file that the group or others may read or write is set back to 0600.</summary>
    public static void Tighten(string path, ILogger logger)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        if ((mode & ~PrivateFileMode) == 0) return;
        File.SetUnixFileMode(path, PrivateFileMode);
        logger.LogWarning("{File} could be read by others (mode {Mode}); set back to 0600", path, Convert.ToString((int)mode, 8));
    }
}
