// Port of sea-g2p src/core/dict.rs.
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Helm.Speech.Vietnamese.Core;

/// <summary>
/// The memory-mapped phoneme dictionary, sea-g2p's "SEAP" format (versions 1 and 2): a header, a NUL-terminated UTF-8
/// string pool, then two sorted index tables that are binary-searched at lookup time. "merged" maps a word to one
/// phoneme string (an English one starts with "&lt;en&gt;"); "common" maps a word both languages claim to a
/// (Vietnamese, English) pair that sentence context decides between. The 62 MB file is mapped, never copied, so a phone
/// keeps only the pages it touches. Keys compare as raw UTF-8 bytes, exactly as Rust's <c>&amp;str</c> ordering does.
/// The map is read-only, so lookups are safe from any number of threads; it must not be disposed while one runs.
/// </summary>
internal sealed unsafe class PhonemeDictionary : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly long _length;
    private readonly uint _stringCount;
    private readonly uint _mergedCount;
    private readonly uint _commonCount;
    private readonly long _stringOffsetsPos;
    private readonly long _mergedPos;
    private readonly long _commonPos;
    private readonly long _stringBase;
    private int _disposed;

    private PhonemeDictionary(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* basePtr, long length)
    {
        _file = file;
        _view = view;
        _base = basePtr;
        _length = length;

        if (length < 32 || basePtr[0] != (byte)'S' || basePtr[1] != (byte)'E' || basePtr[2] != (byte)'A' || basePtr[3] != (byte)'P')
            throw new InvalidDataException("Invalid dictionary format");
        var version = U32(4);
        _stringCount = U32(8);
        _mergedCount = U32(12);
        _commonCount = U32(16);
        _stringOffsetsPos = U32(20);
        _mergedPos = U32(24);
        _commonPos = U32(28);

        if (version >= 2)
        {
            if (length < 48) throw new InvalidDataException("Truncated v2 header");
            var sectionCount = U32(32);
            long sectionsPos = U32(36);
            if (sectionsPos + (long)sectionCount * 12 > length) throw new InvalidDataException("Truncated section table");
            _stringBase = 48;
        }
        else
        {
            _stringBase = 32;
        }

        if (_stringOffsetsPos + (long)_stringCount * 4 > length || _mergedPos + (long)_mergedCount * 8 > length
            || _commonPos + (long)_commonCount * 12 > length)
            throw new InvalidDataException("Truncated dictionary tables");
    }

    /// <summary>Map the dictionary file read-only. Throws <see cref="IOException"/> or <see cref="InvalidDataException"/>.</summary>
    public static PhonemeDictionary Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (length < 32)
        {
            stream.Dispose();
            throw new InvalidDataException("Invalid dictionary format");
        }
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try
            {
                return new PhonemeDictionary(file, view, ptr + view.PointerOffset, length);
            }
            catch
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                throw;
            }
        }
        catch
        {
            view?.Dispose();
            if (file != null) file.Dispose();
            else stream.Dispose();
            throw;
        }
    }

    private uint U32(long pos) =>
        (uint)(_base[pos] | _base[pos + 1] << 8 | _base[pos + 2] << 16 | _base[pos + 3] << 24);

    private ReadOnlySpan<byte> GetString(uint id)
    {
        if (id >= _stringCount) return default;
        long start = _stringBase + U32(_stringOffsetsPos + (long)id * 4);
        if (start >= _length) return default;
        var end = start;
        while (end < _length && _base[end] != 0) end++;
        return new ReadOnlySpan<byte>(_base + start, (int)(end - start));
    }

    private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private long Find(long tablePos, uint count, int rowSize, ReadOnlySpan<byte> key)
    {
        int low = 0, high = (int)count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var row = tablePos + (long)mid * rowSize;
            var cmp = GetString(U32(row)).SequenceCompareTo(key);
            if (cmp == 0) return row;
            if (cmp < 0) low = mid + 1;
            else high = mid - 1;
        }
        return -1;
    }

    private long FindRow(long tablePos, uint count, int rowSize, string word)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var max = Encoding.UTF8.GetMaxByteCount(word.Length);
        if (max <= 512)
        {
            Span<byte> buf = stackalloc byte[max];
            var n = Encoding.UTF8.GetBytes(word, buf);
            return Find(tablePos, count, rowSize, buf[..n]);
        }
        return Find(tablePos, count, rowSize, Encoding.UTF8.GetBytes(word));
    }

    /// <summary>The single phoneme string of a word, or null.</summary>
    public string? LookupMerged(string word)
    {
        var row = FindRow(_mergedPos, _mergedCount, 8, word);
        return row < 0 ? null : Decode(GetString(U32(row + 4)));
    }

    /// <summary>The (Vietnamese, English) readings of a word both languages claim, or null.</summary>
    public (string Vi, string En)? LookupCommon(string word)
    {
        var row = FindRow(_commonPos, _commonCount, 12, word);
        return row < 0 ? null : (Decode(GetString(U32(row + 4))), Decode(GetString(U32(row + 8))));
    }

    /// <summary>Does the English side of the dictionary contain the word? A membership test, not a G2P lookup.</summary>
    public bool HasEnglish(string word) =>
        LookupMerged(word) is { } p && p.StartsWith("<en>", StringComparison.Ordinal) || LookupCommon(word) != null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
