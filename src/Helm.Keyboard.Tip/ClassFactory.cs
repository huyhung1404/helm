using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Helm.Keyboard.Tip.Interop;

namespace Helm.Keyboard.Tip;

/// <summary>What COM asks for the input method's class: makes a <see cref="TextService"/>.</summary>
[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    public int CreateInstance(nint outer, in Guid riid, out nint ppv)
    {
        ppv = 0;
        if (outer != 0) return TsfIds.CLASS_E_NOAGGREGATION;
        try
        {
            return Exports.QueryInterface(new TextService(), riid, out ppv);
        }
        catch (Exception ex)
        {
            Log.Error("create instance", ex);
            return TsfIds.E_FAIL;
        }
    }

    public int LockServer(int fLock) => TsfIds.S_OK;
}
