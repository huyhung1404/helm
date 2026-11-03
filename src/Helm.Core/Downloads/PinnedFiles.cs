namespace Helm.Core.Downloads;

/// <summary>One downloaded file: where it goes (relative, with '/'), where it comes from, and how to tell it arrived whole.</summary>
public sealed record PinnedFile(string Path, string Url, long Size, string Sha256);

/// <summary>
/// A set of files Helm downloads on demand (a voice, a model), each pinned to the exact revision and hash Helm was tested
/// with, in one folder of the data folder. A file counts only after its hash was checked (<see cref="IsInstalled"/> looks
/// for the mark left then), so a cut-off download is never loaded.
/// </summary>
public class PinnedFiles(string folder, IReadOnlyList<PinnedFile> files)
{
    public IReadOnlyList<PinnedFile> Files { get; } = files;

    /// <summary>Everything to download, in bytes.</summary>
    public long TotalSize => Files.Sum(f => f.Size);

    public string Folder { get; } = folder;

    public string PathOf(PinnedFile file) => System.IO.Path.Combine(Folder, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>The mark left once a file's hash was checked.</summary>
    public string CheckedMarkOf(PinnedFile file) => PathOf(file) + ".ok";

    /// <summary>Whether <paramref name="file"/> is in place, whole and checked.</summary>
    public bool Has(PinnedFile file)
    {
        var path = PathOf(file);
        return File.Exists(CheckedMarkOf(file)) && File.Exists(path) && new FileInfo(path).Length == file.Size;
    }

    /// <summary>Every file is in place and checked: what they make up can be loaded.</summary>
    public bool IsInstalled => Files.All(Has);

    /// <summary>Bytes already here (whole files and parts of the one being downloaded), for the progress shown.</summary>
    public long BytesPresent() => Files.Sum(f =>
    {
        if (Has(f)) return f.Size;
        var part = PathOf(f) + ".part";
        return File.Exists(part) ? Math.Min(f.Size, new FileInfo(part).Length) : 0;
    });
}
