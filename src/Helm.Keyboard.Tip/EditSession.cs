using System.Runtime.InteropServices.Marshalling;
using Helm.Typing.Tip.Interop;

namespace Helm.Typing.Tip;

/// <summary>TSF only lets an input method touch the app's text inside an edit session: this runs a callback in one.</summary>
[GeneratedComClass]
internal sealed partial class EditSession(Action<uint> edit) : ITfEditSession
{
    public int DoEditSession(uint ec)
    {
        try
        {
            edit(ec);
            return TsfIds.S_OK;
        }
        catch (Exception ex)
        {
            Log.Error("edit session", ex);
            return TsfIds.E_FAIL;
        }
    }
}
