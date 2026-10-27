using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>A little-endian float32 array read from a NumPy .npy file: its shape and its values in C order.</summary>
public sealed record NpyArray(int[] Shape, float[] Data)
{
    /// <summary>The values from <paramref name="row"/> of a two-dimensional array (or the last axis of a deeper one).</summary>
    public ReadOnlySpan<float> Row(int row)
    {
        var width = Shape[^1];
        return Data.AsSpan(row * width, width);
    }
}

/// <summary>
/// Reads the float32 arrays of a NumPy .npz file (a zip of .npy files, as VieNeu ships its embedding tables). Only what
/// those files use is supported: little-endian float32, C order, any shape.
/// </summary>
public static partial class NpyArchive
{
    public static IReadOnlyDictionary<string, NpyArray> Read(string path)
    {
        var arrays = new Dictionary<string, NpyArray>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".npy", StringComparison.Ordinal)) continue;
            using var stream = entry.Open();
            arrays[entry.Name[..^4]] = ReadArray(stream);
        }
        return arrays;
    }

    private static NpyArray ReadArray(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (reader.ReadByte() != 0x93 || Encoding.ASCII.GetString(reader.ReadBytes(5)) != "NUMPY")
            throw new InvalidDataException("Not a NumPy array.");
        var major = reader.ReadByte();
        reader.ReadByte();
        var headerLength = major == 1 ? reader.ReadUInt16() : (int)reader.ReadUInt32();
        var header = Encoding.ASCII.GetString(reader.ReadBytes(headerLength));
        if (!header.Contains("'<f4'", StringComparison.Ordinal) || header.Contains("'fortran_order': True", StringComparison.Ordinal))
            throw new InvalidDataException("Only little-endian float32 arrays in C order are supported: " + header.Trim());
        var shapeText = ShapePattern().Match(header).Groups[1].Value;
        var shape = shapeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        var count = shape.Aggregate(1L, (a, b) => a * b);
        var data = new float[count];
        var bytes = MemoryMarshal.AsBytes(data.AsSpan());
        var read = 0;
        while (read < bytes.Length)
        {
            var n = stream.Read(bytes[read..]);
            if (n == 0) throw new EndOfStreamException("The array ends early.");
            read += n;
        }
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Big-endian devices are not supported.");
        return new NpyArray(shape, data);
    }

    [GeneratedRegex(@"'shape':\s*\(([^)]*)\)")]
    private static partial Regex ShapePattern();
}
