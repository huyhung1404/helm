using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Helm.Keyboard.Tip.Interop;

namespace Helm.Keyboard.Tip;

/// <summary>
/// How the word being typed looks in the app: exactly like the rest of the text, with no underline, the way a
/// keyboard-hook tool such as EVKey types. Without it, apps underline the composition.
/// </summary>
[GeneratedComClass]
internal sealed partial class DisplayAttribute : ITfDisplayAttributeInfo
{
    public static readonly Guid Id = new("08b95879-f815-404f-8145-26ca9693d72d");

    public int GetGUID(out Guid guid)
    {
        guid = Id;
        return TsfIds.S_OK;
    }

    public int GetDescription(out nint description)
    {
        description = Marshal.StringToBSTR("Helm Keyboard: the word being typed");
        return TsfIds.S_OK;
    }

    public int GetAttributeInfo(out TfDisplayAttribute attribute)
    {
        attribute = default; // no colors of its own, no line, an input attribute
        return TsfIds.S_OK;
    }

    public int SetAttributeInfo(in TfDisplayAttribute attribute) => TsfIds.E_FAIL;

    public int Reset() => TsfIds.S_OK;
}

/// <summary>The list of display attributes TSF asks the input method for: just <see cref="DisplayAttribute"/>.</summary>
[GeneratedComClass]
internal sealed unsafe partial class DisplayAttributeList : IEnumTfDisplayAttributeInfo
{
    private int _next;

    public int Clone(out IEnumTfDisplayAttributeInfo? clone)
    {
        clone = new DisplayAttributeList { _next = _next };
        return TsfIds.S_OK;
    }

    public int Next(uint count, nint* infos, uint* fetched)
    {
        uint given = 0;
        if (count > 0 && _next == 0)
        {
            var hr = Exports.QueryInterface(new DisplayAttribute(), typeof(ITfDisplayAttributeInfo).GUID, out infos[0]);
            if (hr < 0) return hr;
            _next = 1;
            given = 1;
        }
        if (fetched is not null) *fetched = given;
        return given == count ? TsfIds.S_OK : TsfIds.S_FALSE;
    }

    public int Reset()
    {
        _next = 0;
        return TsfIds.S_OK;
    }

    public int Skip(uint count)
    {
        var skipped = Math.Min(count, (uint)(1 - _next));
        _next += (int)skipped;
        return skipped == count ? TsfIds.S_OK : TsfIds.S_FALSE;
    }
}
