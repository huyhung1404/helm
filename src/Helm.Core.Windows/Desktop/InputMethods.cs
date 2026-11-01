using System.Runtime.InteropServices;
using Helm.Core.Interop;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Desktop;

/// <summary>
/// The keyboards in Windows' list (an input language, each with its input method), for Helm Keyboard: which one a
/// window types with, switching it, and adding or removing one as Settings → Language does.
/// </summary>
public interface IInputMethods
{
    /// <summary>The language the window types in (the low word of its keyboard, e.g. 0x0409), 0 when unknown.</summary>
    int GetLanguage(nint hwnd);

    /// <summary>Whether the keyboard list has a keyboard of <paramref name="language"/>.</summary>
    bool HasLanguage(int language);

    /// <summary>Asks the window to type with the keyboard of <paramref name="language"/>; false when there is none.</summary>
    bool RequestLanguage(nint hwnd, int language);

    /// <summary>
    /// Adds <paramref name="inputMethod"/> ("0409:00000409", or "0409:{clsid}{profile}" for an input method) to the
    /// keyboard list, as the default one if asked, or removes it.
    /// </summary>
    bool AddInputMethod(string inputMethod, bool makeDefault = false);

    bool RemoveInputMethod(string inputMethod);

    /// <summary>
    /// Windows' Ctrl+Shift, which switches keyboards within a language: on, Windows takes the Shift of a Ctrl+Shift
    /// before input methods see it.
    /// </summary>
    void SetLayoutSwitchShortcut(bool on);

    /// <summary>A TSF global compartment (a value every app's input method sees). Use it on one STA thread only.</summary>
    IGlobalCompartment OpenGlobalCompartment(Guid id);
}

/// <summary>A number shared with every app's input methods; see <see cref="IInputMethods.OpenGlobalCompartment"/>.</summary>
public interface IGlobalCompartment : IDisposable
{
    /// <summary>The value, null when unset.</summary>
    int? Read();

    bool Write(int value);
}

public sealed class InputMethods : IInputMethods
{
    private const uint ILOT_UNINSTALL = 0x1, ILOT_DEFPROFILE = 0x2;

    public int GetLanguage(nint hwnd)
    {
        unsafe
        {
            var thread = PInvoke.GetWindowThreadProcessId(TypingWindow(hwnd), null);
            return thread == 0 ? 0 : (int)((nint)PInvoke.GetKeyboardLayout(thread).Value & 0xFFFF);
        }
    }

    public bool HasLanguage(int language) => FindKeyboard(language) != 0;

    public bool RequestLanguage(nint hwnd, int language)
    {
        var keyboard = FindKeyboard(language);
        return keyboard != 0 && PInvoke.PostMessage(TypingWindow(hwnd), PInvoke.WM_INPUTLANGCHANGEREQUEST, default, keyboard);
    }

    /// <summary>
    /// The control of <paramref name="hwnd"/>'s thread that has the keyboard, else the window itself. Keyboards are per
    /// thread, and the text box may run on a thread of its own (Notepad's does); a dialog (Character Map) ignores a
    /// switch asked of its frame.
    /// </summary>
    private static HWND TypingWindow(nint hwnd)
    {
        var window = hwnd.ToHwnd();
        unsafe
        {
            var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
            var thread = PInvoke.GetWindowThreadProcessId(window, null);
            return thread != 0 && PInvoke.GetGUIThreadInfo(thread, ref info) && !info.hwndFocus.IsNull ? info.hwndFocus : window;
        }
    }

    public bool AddInputMethod(string inputMethod, bool makeDefault = false) =>
        InstallLayoutOrTip(inputMethod, makeDefault ? ILOT_DEFPROFILE : 0);

    public bool RemoveInputMethod(string inputMethod) => InstallLayoutOrTip(inputMethod, ILOT_UNINSTALL);

    public void SetLayoutSwitchShortcut(bool on)
    {
        // "Layout Hotkey": 2 = Ctrl+Shift (Windows' default), 3 = none. "Hotkey" mirrors the language one (1 = Alt+Shift).
        using (var key = Registry.CurrentUser.CreateSubKey(@"Keyboard Layout\Toggle"))
        {
            key.SetValue("Layout Hotkey", on ? "2" : "3");
            if (key.GetValue("Hotkey") is null) key.SetValue("Hotkey", "1");
            if (key.GetValue("Language Hotkey") is null) key.SetValue("Language Hotkey", "1");
        }
        unsafe
        {
            // Windows reads the toggle keys again.
            PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETLANGTOGGLE, 0, null, 0);
        }
    }

    public IGlobalCompartment OpenGlobalCompartment(Guid id) => new GlobalCompartment(id);

    private static nint FindKeyboard(int language)
    {
        unsafe
        {
            var count = PInvoke.GetKeyboardLayoutList(0, null);
            if (count <= 0) return 0;
            var keyboards = stackalloc HKL[count];
            count = PInvoke.GetKeyboardLayoutList(count, keyboards);
            for (var i = 0; i < count; i++)
                if (((nint)keyboards[i].Value & 0xFFFF) == language) return (nint)keyboards[i].Value;
            return 0;
        }
    }

    [DllImport("input.dll", CharSet = CharSet.Unicode, EntryPoint = "InstallLayoutOrTip")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InstallLayoutOrTip(string inputMethod, uint flags);

    /// <summary>The thread's own TSF thread manager, to reach the global compartments.</summary>
    private sealed class GlobalCompartment : IGlobalCompartment
    {
        private static readonly Guid CLSID_TF_ThreadMgr = new("529a9e6b-6587-4f23-ab9e-9c7d683e3c50");

        private readonly ITfThreadMgr _threadMgr;
        private readonly ITfCompartment _compartment;
        private readonly uint _clientId;

        public GlobalCompartment(Guid id)
        {
            _threadMgr = (ITfThreadMgr)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_TF_ThreadMgr, throwOnError: true)!)!;
            Marshal.ThrowExceptionForHR(_threadMgr.Activate(out _clientId));
            Marshal.ThrowExceptionForHR(_threadMgr.GetGlobalCompartment(out var compartments));
            Marshal.ThrowExceptionForHR(compartments.GetCompartment(id, out _compartment));
        }

        public int? Read() => _compartment.GetValue(out var value) >= 0 && value is int number ? number : null;

        public bool Write(int value)
        {
            object boxed = value;
            return _compartment.SetValue(_clientId, ref boxed) >= 0;
        }

        public void Dispose() => _threadMgr.Deactivate();
    }

    // The parts of msctf.h used here, in header order up to the last method called.

    [ComImport, Guid("aa80e801-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfThreadMgr
    {
        [PreserveSig] int Activate(out uint clientId);
        [PreserveSig] int Deactivate();
        void CreateDocumentMgr();
        void EnumDocumentMgrs();
        void GetFocus();
        void SetFocus();
        void AssociateFocus();
        void IsThreadFocus();
        void GetFunctionProvider();
        void EnumFunctionProviders();
        [PreserveSig] int GetGlobalCompartment(out ITfCompartmentMgr compartments);
    }

    [ComImport, Guid("7dcf57ac-18ad-438b-824d-979bffb74b7c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfCompartmentMgr
    {
        [PreserveSig] int GetCompartment(in Guid id, out ITfCompartment compartment);
    }

    [ComImport, Guid("bb08f7a9-607a-4384-8623-056892b64371"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfCompartment
    {
        [PreserveSig] int SetValue(uint clientId, ref object value);
        [PreserveSig] int GetValue(out object value);
    }
}
