using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Helm.Typing.Tip.Interop;
using Microsoft.Win32;

namespace Helm.Typing.Tip;

/// <summary>
/// The DLL's entry points: COM gets the class factory here, and regsvr32 registers the input method (the COM class,
/// the English (US) "Helm Keyboard" profile and its categories) or removes it. Registering needs administrator rights.
/// </summary>
internal static unsafe class Exports
{
    public static readonly Guid Clsid = KeyboardIds.Clsid;
    public static readonly Guid Profile = KeyboardIds.Profile;
    /// <summary>Helm Keyboard is an English (US) keyboard with a Vietnamese switch, on the US layout (digits stay digits).</summary>
    public const ushort EnglishUs = KeyboardIds.EnglishUs;
    private const uint UsLayout = KeyboardIds.EnglishUs;
    private const string Name = "Helm Keyboard";

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid[] Categories =
        [TsfIds.TipKeyboard, TsfIds.ImmersiveSupport, TsfIds.SysTraySupport, TsfIds.UiElementEnabled, TsfIds.DisplayAttributeProvider];

    public static int QueryInterface(object instance, in Guid iid, out nint ppv)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, iid, out ppv);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    private static int DllGetClassObject(Guid* clsid, Guid* iid, nint* ppv)
    {
        *ppv = 0;
        if (*clsid != Clsid) return TsfIds.CLASS_E_CLASSNOTAVAILABLE;
        try
        {
            var hr = QueryInterface(new ClassFactory(), *iid, out var factory);
            *ppv = factory;
            return hr;
        }
        catch (Exception ex)
        {
            Log.Error("DllGetClassObject", ex);
            return TsfIds.E_FAIL;
        }
    }

    // The native runtime cannot be unloaded from a process.
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    private static int DllCanUnloadNow() => TsfIds.S_FALSE;

    [UnmanagedCallersOnly(EntryPoint = "DllRegisterServer")]
    private static int DllRegisterServer()
    {
        try
        {
            var path = ModulePath();
            using (var key = Registry.LocalMachine.CreateSubKey($@"Software\Classes\CLSID\{{{Clsid}}}"))
            {
                key.SetValue(null, Name);
                using var server = key.CreateSubKey("InprocServer32");
                server.SetValue(null, path);
                server.SetValue("ThreadingModel", "Apartment");
            }
            var profiles = Create<ITfInputProcessorProfileMgr>(TsfIds.InputProcessorProfiles);
            fixed (char* name = Name)
            fixed (char* icon = path)
            {
                Check(profiles.RegisterProfile(Clsid, EnglishUs, Profile, name, (uint)Name.Length, icon, (uint)path.Length, 0, 0, UsLayout, 1, 0),
                    "RegisterProfile");
            }
            var categories = Create<ITfCategoryMgr>(TsfIds.CategoryMgr);
            foreach (var category in Categories) Check(categories.RegisterCategory(Clsid, category, Clsid), "RegisterCategory");
            return TsfIds.S_OK;
        }
        catch (Exception ex)
        {
            Log.Error("DllRegisterServer", ex);
            return ex.HResult < 0 ? ex.HResult : TsfIds.E_FAIL;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "DllUnregisterServer")]
    private static int DllUnregisterServer()
    {
        try
        {
            var categories = Create<ITfCategoryMgr>(TsfIds.CategoryMgr);
            foreach (var category in Categories) categories.UnregisterCategory(Clsid, category, Clsid);
            Create<ITfInputProcessorProfileMgr>(TsfIds.InputProcessorProfiles).UnregisterProfile(Clsid, EnglishUs, Profile, 0);
            Registry.LocalMachine.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{Clsid}}}", throwOnMissingSubKey: false);
            return TsfIds.S_OK;
        }
        catch (Exception ex)
        {
            Log.Error("DllUnregisterServer", ex);
            return ex.HResult < 0 ? ex.HResult : TsfIds.E_FAIL;
        }
    }

    public static T Create<T>(Guid clsid)
    {
        Check(Native.CoCreateInstance(clsid, 0, Native.CLSCTX_INPROC_SERVER, typeof(T).GUID, out var instance), $"CoCreateInstance {typeof(T).Name}");
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException(what, hr);
    }

    public static string ModulePath()
    {
        delegate* unmanaged<int> inThisModule = &DllCanUnloadNow;
        if (!Native.GetModuleHandleEx(Native.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | Native.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                (nint)inThisModule, out var module))
            throw new COMException("GetModuleHandleEx", Marshal.GetHRForLastWin32Error());
        var buffer = stackalloc char[1024];
        var length = Native.GetModuleFileName(module, buffer, 1024);
        return new string(buffer, 0, (int)length);
    }
}
