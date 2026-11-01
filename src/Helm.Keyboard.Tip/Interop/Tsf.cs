using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Helm.Typing.Tip.Interop;

// The parts of the Text Services Framework (msctf.h) Helm Keyboard uses. A COM interface is a table of functions, so
// every method is declared in the order of the header, up to the last one called here; methods never called are
// placeholders that only keep the slots (_n).

internal static class TsfIds
{
    public static readonly Guid TipKeyboard = new("34745c63-b2f0-4784-8b67-5e12c8701a31");          // GUID_TFCAT_TIP_KEYBOARD
    public static readonly Guid ImmersiveSupport = new("13a016df-560b-46cd-947a-4c3af1e0e35d");     // GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT
    public static readonly Guid SysTraySupport = new("25504fb4-7bab-4bc1-9c69-cf81890f0ef5");       // GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT
    public static readonly Guid UiElementEnabled = new("49d2f9cf-1f5e-11d7-a6d3-00065b84435c");     // GUID_TFCAT_TIPCAP_UIELEMENTENABLED
    public static readonly Guid DisplayAttributeProvider = new("046b8c80-1647-40f7-9b21-b93b81aabc1b"); // GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER
    public static readonly Guid PropAttribute = new("34b45670-7526-11d2-a147-00105a2799b5");        // GUID_PROP_ATTRIBUTE
    public static readonly Guid PropInputScope = new("1713dd5a-68e7-4a5b-9af6-592a595c778d");       // GUID_PROP_INPUTSCOPE
    public const int IS_URL = 1;
    public static readonly Guid InputProcessorProfiles = new("33c53a50-f456-4884-b049-85fd643ecfed"); // CLSID_TF_InputProcessorProfiles
    public static readonly Guid CategoryMgr = new("a4b544a1-438d-4b41-9325-869523e2d6c7");          // CLSID_TF_CategoryMgr
    public static readonly Guid InputModeItem = new("2c77a81e-41cc-4178-a3a7-5f8a987568e6");        // GUID_LBI_INPUTMODE
    public static readonly Guid ThreadMgrEventSink = typeof(ITfThreadMgrEventSink).GUID;
    public static readonly Guid CompartmentEventSink = typeof(ITfCompartmentEventSink).GUID;
    public static readonly Guid LangBarItemSink = typeof(ITfLangBarItemSink).GUID;

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const int CONNECT_E_CANNOTCONNECT = unchecked((int)0x80040202);
    public const int CONNECT_E_ADVISELIMIT = unchecked((int)0x80040201);
    public const int TF_E_SYNCHRONOUS = unchecked((int)0x80040505);

    public const uint TF_ES_ASYNC = 0x8;
    public const uint TF_ES_SYNC = 0x1;
    public const uint TF_ES_READWRITE = 0x6;
    public const uint TF_IAS_QUERYONLY = 0x2;
    public const int TF_ANCHOR_END = 1;
    public const uint TF_INVALID_COOKIE = 0xffffffff;

    public const uint TF_LBI_STYLE_SHOWNINTRAY = 0x2;
    public const uint TF_LBI_STYLE_BTN_BUTTON = 0x10000;
    public const uint TF_LBI_BTNALL = 0x1 | 0x2 | 0x4; // icon, text, tooltip
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct TfLangBarItemInfo
{
    public Guid Service;
    public Guid Item;
    public uint Style;
    public uint Sort;
    public fixed char Description[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct TfPoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TfSelection
{
    public nint Range;          // ITfRange*
    public int ActiveSelEnd;    // TF_SELECTIONSTYLE.ase
    public int InterimChar;     // TF_SELECTIONSTYLE.fInterimChar
}

[StructLayout(LayoutKind.Sequential)]
internal struct TfDaColor
{
    public int Type;            // TF_DA_COLORTYPE: 0 = none, the app's own color
    public uint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TfDisplayAttribute
{
    public TfDaColor Text;
    public TfDaColor Background;
    public int LineStyle;       // TF_DA_LINESTYLE: 0 = none
    public int BoldLine;
    public TfDaColor Line;
    public int Attr;            // TF_DA_ATTR_INFO: 0 = input
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-c000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid riid, out nint ppv);
    [PreserveSig] int LockServer(int fLock);
}

[StructLayout(LayoutKind.Sequential)]
internal struct TfPreservedKey
{
    public uint VirtualKey;
    public uint Modifiers;      // TF_MOD_ALT 0x1, TF_MOD_CONTROL 0x2, TF_MOD_SHIFT 0x4
}

[GeneratedComInterface]
[Guid("aa80e801-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfThreadMgr
{
    [PreserveSig] int _1();
    [PreserveSig] int _2();
    [PreserveSig] int _3();
    [PreserveSig] int _4();
    [PreserveSig] int _5();
    [PreserveSig] int _6();
    [PreserveSig] int _7();
    [PreserveSig] int _8();
    [PreserveSig] int _9();
    [PreserveSig] int _10();
    [PreserveSig] int GetGlobalCompartment(out ITfCompartmentMgr? compartments);
}

[GeneratedComInterface]
[Guid("7dcf57ac-18ad-438b-824d-979bffb74b7c")]
internal partial interface ITfCompartmentMgr
{
    [PreserveSig] int GetCompartment(in Guid guid, out ITfCompartment? compartment);
}

[GeneratedComInterface]
[Guid("bb08f7a9-607a-4384-8623-056892b64371")]
internal unsafe partial interface ITfCompartment
{
    [PreserveSig] int SetValue(uint clientId, ComVariant* value);
    [PreserveSig] int GetValue(ComVariant* value);
}

[GeneratedComInterface]
[Guid("743abd5f-f26d-48df-8cc5-238492419b64")]
internal partial interface ITfCompartmentEventSink
{
    [PreserveSig] int OnChange(in Guid guid);
}

[GeneratedComInterface]
[Guid("aa80e7f7-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfTextInputProcessor
{
    [PreserveSig] int Activate(ITfThreadMgr threadMgr, uint clientId);
    [PreserveSig] int Deactivate();
}

[GeneratedComInterface]
[Guid("6e4e2102-f9cd-433d-b496-303ce03a6507")]
internal partial interface ITfTextInputProcessorEx : ITfTextInputProcessor
{
    [PreserveSig] int ActivateEx(ITfThreadMgr threadMgr, uint clientId, uint flags);
}

[GeneratedComInterface]
[Guid("aa80e7f0-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfKeystrokeMgr
{
    [PreserveSig] int AdviseKeyEventSink(uint clientId, ITfKeyEventSink sink, int foreground);
    [PreserveSig] int UnadviseKeyEventSink(uint clientId);
    [PreserveSig] int _3();
    [PreserveSig] int _4();
    [PreserveSig] int _5();
    [PreserveSig] int _6();
    [PreserveSig] int _7();
    [PreserveSig] int _8();
    [PreserveSig] int _9();
    [PreserveSig] unsafe int PreserveKey(uint clientId, in Guid guid, in TfPreservedKey key, char* description, uint length);
    [PreserveSig] int UnpreserveKey(in Guid guid, in TfPreservedKey key);
}

[GeneratedComInterface]
[Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfKeyEventSink
{
    [PreserveSig] int OnSetFocus(int foreground);
    [PreserveSig] int OnTestKeyDown(ITfContext? context, nuint wParam, nint lParam, out int eaten);
    [PreserveSig] int OnTestKeyUp(ITfContext? context, nuint wParam, nint lParam, out int eaten);
    [PreserveSig] int OnKeyDown(ITfContext? context, nuint wParam, nint lParam, out int eaten);
    [PreserveSig] int OnKeyUp(ITfContext? context, nuint wParam, nint lParam, out int eaten);
    [PreserveSig] int OnPreservedKey(ITfContext? context, in Guid guid, out int eaten);
}

[GeneratedComInterface]
[Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7")]
internal partial interface ITfSource
{
    [PreserveSig] int AdviseSink(in Guid riid, nint sink, out uint cookie);
    [PreserveSig] int UnadviseSink(uint cookie);
}

[GeneratedComInterface]
[Guid("aa80e7f4-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfDocumentMgr;

[GeneratedComInterface]
[Guid("aa80e80e-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfThreadMgrEventSink
{
    [PreserveSig] int OnInitDocumentMgr(ITfDocumentMgr? documentMgr);
    [PreserveSig] int OnUninitDocumentMgr(ITfDocumentMgr? documentMgr);
    [PreserveSig] int OnSetFocus(ITfDocumentMgr? focus, ITfDocumentMgr? previous);
    [PreserveSig] int OnPushContext(ITfContext? context);
    [PreserveSig] int OnPopContext(ITfContext? context);
}

[GeneratedComInterface]
[Guid("aa80e7fd-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfContext
{
    [PreserveSig] int RequestEditSession(uint clientId, ITfEditSession session, uint flags, out int hrSession);
    [PreserveSig] int _2();
    [PreserveSig] int _3();
    [PreserveSig] int SetSelection(uint ec, uint count, in TfSelection selection);
    [PreserveSig] int _5();
    [PreserveSig] int _6();
    [PreserveSig] int _7();
    [PreserveSig] int _8();
    [PreserveSig] int _9();
    [PreserveSig] int GetProperty(in Guid guid, out ITfProperty? property);
    [PreserveSig] int GetAppProperty(in Guid guid, out ITfReadOnlyProperty? property);
}

[GeneratedComInterface]
[Guid("17d49a3d-f8b8-4b2f-b254-52319dd64c53")]
internal partial interface ITfReadOnlyProperty
{
    [PreserveSig] int _1();
    [PreserveSig] int _2();
    [PreserveSig] unsafe int GetValue(uint ec, ITfRange range, ComVariant* value);
}

[GeneratedComInterface]
[Guid("e2449660-9542-11d2-bf46-00105a2799b5")]
internal partial interface ITfProperty
{
    // ITfReadOnlyProperty
    [PreserveSig] int _1();
    [PreserveSig] int _2();
    [PreserveSig] unsafe int GetValue(uint ec, ITfRange range, ComVariant* value);
    [PreserveSig] int _4();
    // ITfProperty
    [PreserveSig] int _5();
    [PreserveSig] int _6();
    [PreserveSig] unsafe int SetValue(uint ec, ITfRange range, ComVariant* value);
    [PreserveSig] int Clear(uint ec, ITfRange? range);
}

[GeneratedComInterface]
[Guid("fde1eaee-6924-4cdf-91e7-da38cff5559d")]
internal unsafe partial interface ITfInputScope
{
    [PreserveSig] int GetInputScopes(int** scopes, uint* count);
}

[GeneratedComInterface]
[Guid("fee47777-163c-4769-996a-6e9c50ad8f54")]
internal partial interface ITfDisplayAttributeProvider
{
    [PreserveSig] int EnumDisplayAttributeInfo(out IEnumTfDisplayAttributeInfo? enumerator);
    [PreserveSig] int GetDisplayAttributeInfo(in Guid guid, out ITfDisplayAttributeInfo? info);
}

[GeneratedComInterface]
[Guid("7cef04d7-cb75-4e80-a7ab-5f5bc7d332de")]
internal unsafe partial interface IEnumTfDisplayAttributeInfo
{
    [PreserveSig] int Clone(out IEnumTfDisplayAttributeInfo? clone);
    [PreserveSig] int Next(uint count, nint* infos, uint* fetched);
    [PreserveSig] int Reset();
    [PreserveSig] int Skip(uint count);
}

[GeneratedComInterface]
[Guid("70528852-2f26-4aea-8c96-215150578932")]
internal partial interface ITfDisplayAttributeInfo
{
    [PreserveSig] int GetGUID(out Guid guid);
    [PreserveSig] int GetDescription(out nint description);
    [PreserveSig] int GetAttributeInfo(out TfDisplayAttribute attribute);
    [PreserveSig] int SetAttributeInfo(in TfDisplayAttribute attribute);
    [PreserveSig] int Reset();
}

[GeneratedComInterface]
[Guid("aa80e803-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfEditSession
{
    [PreserveSig] int DoEditSession(uint ec);
}

[GeneratedComInterface]
[Guid("aa80e7ff-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfRange
{
    [PreserveSig] unsafe int GetText(uint ec, uint flags, char* text, uint max, out uint length);
    [PreserveSig] unsafe int SetText(uint ec, uint flags, char* text, int length);
    [PreserveSig] int _3();
    [PreserveSig] int _4();
    [PreserveSig] int _5();
    [PreserveSig] int ShiftStart(uint ec, int count, out int moved, nint halt);
    [PreserveSig] int _7();
    [PreserveSig] int _8();
    [PreserveSig] int _9();
    [PreserveSig] int _10();
    [PreserveSig] int _11();
    [PreserveSig] int IsEmpty(uint ec, out int empty);
    [PreserveSig] int Collapse(uint ec, int anchor);
}

[GeneratedComInterface]
[Guid("55ce16ba-3014-41c1-9ceb-fade1446ac6c")]
internal partial interface ITfInsertAtSelection
{
    [PreserveSig] int InsertTextAtSelection(uint ec, uint flags, nint text, int length, out ITfRange? range);
}

[GeneratedComInterface]
[Guid("d40c8aae-ac92-4fc7-9a11-0ee0e23aa39b")]
internal partial interface ITfContextComposition
{
    [PreserveSig] int StartComposition(uint ec, ITfRange range, ITfCompositionSink sink, out ITfComposition? composition);
}

[GeneratedComInterface]
[Guid("20168d64-5a8f-4a5a-b7bd-cfa29f4d0fd9")]
internal partial interface ITfComposition
{
    [PreserveSig] int GetRange(out ITfRange? range);
    [PreserveSig] int _2();
    [PreserveSig] int _3();
    [PreserveSig] int EndComposition(uint ec);
}

[GeneratedComInterface]
[Guid("a781718c-579a-4b15-a280-32b8577acc5e")]
internal partial interface ITfCompositionSink
{
    [PreserveSig] int OnCompositionTerminated(uint ec, ITfComposition? composition);
}

[GeneratedComInterface]
[Guid("ba468c55-9956-4fb1-a59d-52a7dd7cc6aa")]
internal partial interface ITfLangBarItemMgr
{
    [PreserveSig] int _1();
    [PreserveSig] int _2();
    [PreserveSig] int AddItem(ITfLangBarItem item);
    [PreserveSig] int RemoveItem(ITfLangBarItem item);
}

[GeneratedComInterface]
[Guid("73540d69-edeb-4ee9-96c9-23aa30b25916")]
internal unsafe partial interface ITfLangBarItem
{
    [PreserveSig] int GetInfo(TfLangBarItemInfo* info);
    [PreserveSig] int GetStatus(out uint status);
    [PreserveSig] int Show(int show);
    [PreserveSig] int GetTooltipString(out nint tooltip);
}

[GeneratedComInterface]
[Guid("28c7f1d0-de25-11d2-afdd-00105a2799b5")]
internal unsafe partial interface ITfLangBarItemButton : ITfLangBarItem
{
    [PreserveSig] int OnClick(int click, TfPoint point, nint area);
    [PreserveSig] int InitMenu(nint menu);
    [PreserveSig] int OnMenuSelect(uint id);
    [PreserveSig] int GetIcon(out nint icon);
    [PreserveSig] int GetText(out nint text);
}

[GeneratedComInterface]
[Guid("57dbe1a0-de25-11d2-afdd-00105a2799b5")]
internal partial interface ITfLangBarItemSink
{
    [PreserveSig] int OnUpdate(uint flags);
}

[GeneratedComInterface]
[Guid("71c6e74c-0f28-11d8-a82a-00065b84435c")]
internal partial interface ITfInputProcessorProfileMgr
{
    [PreserveSig] int _1();
    [PreserveSig] int _2();
    [PreserveSig] int _3();
    [PreserveSig] int _4();
    [PreserveSig] int _5();
    [PreserveSig] unsafe int RegisterProfile(in Guid clsid, ushort langId, in Guid profile, char* description, uint descriptionLength,
        char* iconFile, uint iconFileLength, uint iconIndex, nint hklSubstitute, uint preferredLayout, int enabledByDefault, uint flags);
    [PreserveSig] int UnregisterProfile(in Guid clsid, ushort langId, in Guid profile, uint flags);
}

[GeneratedComInterface]
[Guid("c3acefb5-f69d-4905-938f-fcadcf4be830")]
internal partial interface ITfCategoryMgr
{
    [PreserveSig] int RegisterCategory(in Guid clsid, in Guid category, in Guid item);
    [PreserveSig] int UnregisterCategory(in Guid clsid, in Guid category, in Guid item);
    [PreserveSig] int _3();
    [PreserveSig] int _4();
    [PreserveSig] int _5();
    [PreserveSig] int _6();
    [PreserveSig] int _7();
    [PreserveSig] int _8();
    [PreserveSig] int _9();
    [PreserveSig] int _10();
    [PreserveSig] int _11();
    [PreserveSig] int RegisterGUID(in Guid guid, out uint atom);
}
