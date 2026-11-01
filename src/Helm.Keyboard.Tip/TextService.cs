using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Helm.Typing.Tip.Interop;

namespace Helm.Typing.Tip;

/// <summary>
/// Helm Keyboard inside an app: one instance per app thread the user types in. It is the English keyboard with a
/// Vietnamese switch (V/E, like EVKey), shared by every app through a TSF global compartment that Helm also sets.
/// With V on, letters go into a TelexWord whose text replaces the word in the app's text through TSF, so it looks like
/// any typed text (no composition underline). Apps whose text TSF cannot edit get the word as a composition instead;
/// any other key then hands it over (commits it) before the app sees that key. Either way the app never gets a fake
/// Backspace, which is what broke Chrome's address bar with keyboard-hook typing tools ("dđại học").
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class TextService
    : ITfTextInputProcessorEx, ITfKeyEventSink, ITfThreadMgrEventSink, ITfCompositionSink, ITfDisplayAttributeProvider,
      ITfCompartmentEventSink
{


    /// <summary>The longest word taken back from before the caret.</summary>
    private const int LongestWord = 16;

    private enum KeyKind
    {
        Ignored,
        Letter,
        Backspace,
        Other,
    }

    private readonly TelexWord _word = new(TelexOptions.Default);
    /// <summary>Keys whose key-down was eaten: their key-up is eaten too, so the app never sees half a keystroke.</summary>
    private readonly HashSet<int> _eatenKeys = [];
    private ITfThreadMgr? _threadMgr;
    private ITfCompartment? _switch;
    /// <summary><see cref="KeyboardIds.HelmSwitching"/>: Helm has the shortcut while it is 1.</summary>
    private ITfCompartment? _helmSwitching;
    private uint _clientId;
    private uint _threadMgrSinkCookie = TsfIds.TF_INVALID_COOKIE;
    private uint _switchSinkCookie = TsfIds.TF_INVALID_COOKIE;
    private bool _vietnamese;
    /// <summary>
    /// Left Ctrl+Shift, held together and let go, flip V/E (like EVKey), even while Helm is not running. A key pressed
    /// while Ctrl or Shift is held (Ctrl+Shift+T…) makes it the app's shortcut instead: the chord is spoiled until
    /// both are up again.
    /// </summary>
    private bool _chordSpoiled;
    private ModeButton? _modeButton;
    /// <summary>The word as the app has it, when it was typed straight into the app's text.</summary>
    private string _typed = "";
    /// <summary>
    /// Words go in as compositions in the focused text box: TSF cannot edit its text, or it is an address bar, whose
    /// suggestion (the selected rest of an address) Chromium keeps out of TSF's text, so replacing a word goes wrong
    /// ("d" + "ich" → "dđh"); a composition replaces it right.
    /// </summary>
    private bool _composeOnly;
    /// <summary>The focused text box's kind (an address bar or not) is known.</summary>
    private bool _kindKnown;
    private ITfComposition? _composition;
    private ITfContext? _compositionContext;
    /// <summary>TSF's number for <see cref="DisplayAttribute"/>; 0 when it could not be had (the app then underlines).</summary>
    private uint _displayAttribute;

    public int Activate(ITfThreadMgr threadMgr, uint clientId) => ActivateEx(threadMgr, clientId, 0);

    public int ActivateEx(ITfThreadMgr threadMgr, uint clientId, uint flags)
    {
        try
        {
            _threadMgr = threadMgr;
            _clientId = clientId;
            _word.Options = Settings.Load();
            var keystrokes = (ITfKeystrokeMgr)threadMgr;
            Check(keystrokes.AdviseKeyEventSink(clientId, this, 1), "AdviseKeyEventSink");
            _threadMgrSinkCookie = Advise(threadMgr, this, TsfIds.ThreadMgrEventSink);
            Exports.Create<ITfCategoryMgr>(TsfIds.CategoryMgr).RegisterGUID(DisplayAttribute.Id, out _displayAttribute);
            threadMgr.GetGlobalCompartment(out var compartments);
            if (compartments is not null && compartments.GetCompartment(KeyboardIds.VietnameseSwitch, out _switch) >= 0 && _switch is not null)
            {
                _switchSinkCookie = Advise(_switch, this, TsfIds.CompartmentEventSink);
                _vietnamese = ReadSwitch();
            }
            compartments?.GetCompartment(KeyboardIds.HelmSwitching, out _helmSwitching);
            _modeButton = new ModeButton(Flip);
            _modeButton.Show(_vietnamese);
            ((ITfLangBarItemMgr)threadMgr).AddItem(_modeButton);
            Log.Info($"activated in {Environment.ProcessPath} ({Exports.ModulePath()}), Vietnamese {(_vietnamese ? "on" : "off")}");
            return TsfIds.S_OK;
        }
        catch (Exception ex)
        {
            Log.Error("activate", ex);
            Deactivate();
            return TsfIds.E_FAIL;
        }
    }

    public int Deactivate()
    {
        try
        {
            CommitLater();
            if (_switch is not null && _switchSinkCookie != TsfIds.TF_INVALID_COOKIE) ((ITfSource)_switch).UnadviseSink(_switchSinkCookie);
            if (_threadMgr is { } threadMgr)
            {
                if (_modeButton is not null) ((ITfLangBarItemMgr)threadMgr).RemoveItem(_modeButton);
                ((ITfKeystrokeMgr)threadMgr).UnadviseKeyEventSink(_clientId);
                if (_threadMgrSinkCookie != TsfIds.TF_INVALID_COOKIE) ((ITfSource)threadMgr).UnadviseSink(_threadMgrSinkCookie);
            }
        }
        catch (Exception ex)
        {
            Log.Error("deactivate", ex);
        }
        _threadMgrSinkCookie = _switchSinkCookie = TsfIds.TF_INVALID_COOKIE;
        _switch = null;
        _helmSwitching = null;
        _modeButton = null;
        _threadMgr = null;
        return TsfIds.S_OK;
    }

    // ITfKeyEventSink

    public int OnSetFocus(int foreground) => TsfIds.S_OK;

    public int OnTestKeyDown(ITfContext? context, nuint wParam, nint lParam, out int eaten)
    {
        eaten = 0;
        try
        {
            WatchKeyDown((int)wParam, "test key down");
            if (context is null) return TsfIds.S_OK;
            switch (Classify((int)wParam, out _))
            {
                case KeyKind.Letter:
                    eaten = _vietnamese ? 1 : 0;
                    break;
                case KeyKind.Backspace:
                    eaten = _word.IsEmpty ? 0 : 1;
                    break;
                case KeyKind.Other:
                    // Hand the word over now, so the app gets it before the key (a space, a comma, Enter, an arrow…).
                    Commit(context);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("test key down", ex);
        }
        return TsfIds.S_OK;
    }

    public int OnKeyDown(ITfContext? context, nuint wParam, nint lParam, out int eaten)
    {
        eaten = 0;
        try
        {
            WatchKeyDown((int)wParam, "key down");
            if (context is null) return TsfIds.S_OK;
            var virtualKey = (int)wParam;
            switch (Classify(virtualKey, out var letter))
            {
                case KeyKind.Letter when _vietnamese:
                    if (!_kindKnown) Edit(context, ec => LearnKind(context, ec), sync: true);
                    if (_composeOnly)
                    {
                        var resumed = _word.IsEmpty ? Resume(context) : null;
                        _word.Type(letter);
                        Show(context, resumed);
                    }
                    else
                    {
                        Edit(context, ec => TypeInto(context, ec, letter));
                    }
                    eaten = 1;
                    break;
                case KeyKind.Backspace:
                    if (_word.IsEmpty) break;
                    if (_composition is not null)
                    {
                        _word.Backspace();
                        Show(context);
                    }
                    else
                    {
                        Edit(context, ec => BackspaceInto(context, ec));
                    }
                    eaten = 1;
                    break;
                case KeyKind.Other:
                    Commit(context);
                    break;
            }
            if (eaten != 0) _eatenKeys.Add(virtualKey);
            if (Log.IsVerbose) Log.Info($"key 0x{wParam:X2}: '{_word.Text}'");
        }
        catch (Exception ex)
        {
            Log.Error("key down", ex);
        }
        return TsfIds.S_OK;
    }

    public int OnTestKeyUp(ITfContext? context, nuint wParam, nint lParam, out int eaten)
    {
        eaten = _eatenKeys.Contains((int)wParam) ? 1 : 0;
        WatchKeyUp((int)wParam, lParam, "test key up");
        return TsfIds.S_OK;
    }

    public int OnKeyUp(ITfContext? context, nuint wParam, nint lParam, out int eaten)
    {
        eaten = _eatenKeys.Remove((int)wParam) ? 1 : 0;
        WatchKeyUp((int)wParam, lParam, "key up");
        return TsfIds.S_OK;
    }

    public int OnPreservedKey(ITfContext? context, in Guid guid, out int eaten)
    {
        eaten = 0;
        return TsfIds.S_OK;
    }

    // ITfCompartmentEventSink: the V/E switch moved (the shortcut, or Helm).

    public int OnChange(in Guid guid)
    {
        try
        {
            if (guid != KeyboardIds.VietnameseSwitch) return TsfIds.S_OK;
            _vietnamese = ReadSwitch();
            if (!_vietnamese) CommitLater();
            _modeButton?.Show(_vietnamese);
            if (Log.IsVerbose) Log.Info($"Vietnamese {(_vietnamese ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            Log.Error("V/E change", ex);
        }
        return TsfIds.S_OK;
    }

    // ITfThreadMgrEventSink: when the focus moves to another text box, the word stays where it was typed.

    public int OnInitDocumentMgr(ITfDocumentMgr? documentMgr) => TsfIds.S_OK;

    public int OnUninitDocumentMgr(ITfDocumentMgr? documentMgr) => TsfIds.S_OK;

    public int OnSetFocus(ITfDocumentMgr? focus, ITfDocumentMgr? previous)
    {
        try
        {
            CommitLater();
            _composeOnly = false;
            _kindKnown = false;
        }
        catch (Exception ex)
        {
            Log.Error("focus change", ex);
        }
        return TsfIds.S_OK;
    }

    public int OnPushContext(ITfContext? context) => TsfIds.S_OK;

    public int OnPopContext(ITfContext? context) => TsfIds.S_OK;

    // ITfDisplayAttributeProvider: what the attribute set on the word means (asked for by the app's side of TSF).

    public int EnumDisplayAttributeInfo(out IEnumTfDisplayAttributeInfo? enumerator)
    {
        enumerator = new DisplayAttributeList();
        return TsfIds.S_OK;
    }

    public int GetDisplayAttributeInfo(in Guid guid, out ITfDisplayAttributeInfo? info)
    {
        info = guid == DisplayAttribute.Id ? new DisplayAttribute() : null;
        return info is null ? TsfIds.E_INVALIDARG : TsfIds.S_OK;
    }

    // ITfCompositionSink: the app ended the word itself; it keeps the text, and the next letter takes it back.

    public int OnCompositionTerminated(uint ec, ITfComposition? composition)
    {
        if (Log.IsVerbose) Log.Info($"the app ended the word '{_word.Text}'");
        _composition = null;
        _compositionContext = null;
        Forget();
        return TsfIds.S_OK;
    }

    private static KeyKind Classify(int virtualKey, out char letter)
    {
        letter = '\0';
        if (virtualKey is Native.VK_SHIFT or Native.VK_CONTROL or Native.VK_MENU or Native.VK_CAPITAL or Native.VK_LWIN or Native.VK_RWIN
            or >= Native.VK_LSHIFT and <= Native.VK_RMENU)
            return KeyKind.Ignored;
        // Shortcuts (Ctrl+C, Alt+F4…) are the app's.
        if (Native.IsDown(Native.VK_CONTROL) || Native.IsDown(Native.VK_MENU)) return KeyKind.Other;
        if (virtualKey is >= 'A' and <= 'Z')
        {
            var upper = Native.IsDown(Native.VK_SHIFT) ^ Native.IsToggled(Native.VK_CAPITAL);
            letter = (char)(upper ? virtualKey : virtualKey + ('a' - 'A'));
            return KeyKind.Letter;
        }
        return virtualKey == Native.VK_BACK ? KeyKind.Backspace : KeyKind.Other;
    }

    // Apps differ in which of the four key calls they make (Notepad skips the tests for Ctrl and Shift), and a key can
    // come through two of them: the shortcut is watched in all four, and only needs to see keys go up.

    private void WatchKeyDown(int virtualKey, string call)
    {
        if (Log.IsVerbose) Log.Info($"{call} 0x{virtualKey:X2}");
        if (!IsCtrl(virtualKey) && !IsShift(virtualKey) && (Native.IsDown(Native.VK_CONTROL) || Native.IsDown(Native.VK_SHIFT)))
            _chordSpoiled = true;
    }

    private void WatchKeyUp(int virtualKey, nint lParam, string call)
    {
        try
        {
            if (Log.IsVerbose) Log.Info($"{call} 0x{virtualKey:X2}{(_chordSpoiled ? ", spoiled" : "")}");
            var ctrl = IsCtrl(virtualKey);
            if (!ctrl && !IsShift(virtualKey)) return;
            // Let go of one, the other still held; while Helm runs, the shortcut is Helm's (EN / VI / 中).
            if (!_chordSpoiled && !HelmSwitches() && IsLeft(virtualKey, lParam) && Native.IsDown(ctrl ? Native.VK_LSHIFT : Native.VK_LCONTROL)
                && !Native.IsDown(Native.VK_RCONTROL) && !Native.IsDown(Native.VK_RSHIFT) && !Native.IsDown(Native.VK_MENU)
                && !Native.IsDown(Native.VK_LWIN) && !Native.IsDown(Native.VK_RWIN))
            {
                _chordSpoiled = true; // once per chord, however many calls bring this key
                Flip();
            }
            if (!Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_SHIFT)) _chordSpoiled = false;
        }
        catch (Exception ex)
        {
            Log.Error("V/E shortcut", ex);
        }
    }

    private static bool IsCtrl(int virtualKey) => virtualKey is Native.VK_CONTROL or Native.VK_LCONTROL or Native.VK_RCONTROL;

    private static bool IsShift(int virtualKey) => virtualKey is Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT;

    /// <summary>Whether the Ctrl or Shift key is the left one: right Ctrl is an extended key, right Shift has its own scan code.</summary>
    private static bool IsLeft(int virtualKey, nint lParam) => virtualKey switch
    {
        Native.VK_LCONTROL or Native.VK_LSHIFT => true,
        Native.VK_RCONTROL or Native.VK_RSHIFT => false,
        Native.VK_CONTROL => (lParam & (1 << 24)) == 0,
        _ => ((lParam >> 16) & 0xFF) != 0x36,
    };

    private bool ReadSwitch() => IsOn(_switch);

    private static bool IsOn(ITfCompartment? compartment) => Read(compartment) != 0;

    private static int Read(ITfCompartment? compartment)
    {
        if (compartment is null) return 0;
        var value = default(ComVariant);
        try
        {
            return compartment.GetValue(&value) >= 0 && value.VarType == VarEnum.VT_I4 ? value.As<int>() : 0;
        }
        finally
        {
            value.Dispose();
        }
    }

    /// <summary>Helm runs and switches keyboards with its own shortcut (<see cref="KeyboardIds.HelmSwitching"/>).</summary>
    private bool HelmSwitches()
    {
        var process = Read(_helmSwitching);
        if (process <= 0) return false;
        var handle = Native.OpenProcess(Native.SYNCHRONIZE, 0, (uint)process);
        // An app in a sandbox may not look at Helm (running as administrator): it lives, then.
        if (handle == 0) return Marshal.GetLastPInvokeError() == Native.ERROR_ACCESS_DENIED;
        try
        {
            return Native.WaitForSingleObject(handle, 0) == Native.WAIT_TIMEOUT;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Takes back the word right before the caret, so typing goes on with it: after Backspace into a word to fix it
    /// ("vieet", space, Backspace, j → việt), or after an app ended the word by itself. The word is typed again from
    /// its keys and returned, so the composition can be started over it.
    /// </summary>
    private string? Resume(ITfContext context)
    {
        string? text = null;
        Edit(context, ec => text = WordBeforeCaret(context, ec), sync: true);
        if (text is null || Telex.Encode(text) is not { } keys || Telex.Transform(keys, _word.Options) != text) return null;
        foreach (var key in keys) _word.Type(key);
        if (Log.IsVerbose) Log.Info($"taking back '{text}'");
        return text;
    }

    /// <summary>Puts the word as it is now into the app; an empty word ends the composition.</summary>
    private void Show(ITfContext context, string? resumed = null)
    {
        var text = _word.Text;
        Edit(context, ec => SetText(context, ec, text, end: text.Length == 0, resumed));
    }

    /// <summary>Flips V/E for every app: each hears it through <see cref="OnChange"/>, this one too.</summary>
    private void Flip()
    {
        if (_switch is null) return;
        var value = ComVariant.Create(_vietnamese ? 0 : 1);
        _switch.SetValue(_clientId, &value);
    }

    /// <summary>
    /// Types a letter straight into the app's text: the word, as it is now, replaces the word as the app has it. When
    /// that word is not right before the caret any more (the caret moved), a new word starts there, taking back the
    /// word before the caret if there is one.
    /// </summary>
    private void TypeInto(ITfContext context, uint ec, char letter)
    {
        if (Log.IsVerbose) Log.Info($"typing '{letter}' after '{_typed}', the app has '{Around(context, ec)}'");
        var range = _word.IsEmpty ? null : RangeBeforeCaret(context, ec, _typed);
        string? takenBack = null;
        if (range is null)
        {
            Forget();
            range = TakeBack(context, ec, out takenBack) ?? RangeAtCaret(context, ec);
        }
        _word.Type(letter);
        if (range is not null && Replace(context, ec, range, _word.Text))
        {
            if (Log.IsVerbose) Log.Info($"now '{Around(context, ec)}'");
            return;
        }
        // The app's text can't be edited, only composed into.
        if (Log.IsVerbose) Log.Info("the app's text is not editable: composing");
        _composeOnly = true;
        _typed = "";
        SetText(context, ec, _word.Text, end: false, takenBack);
    }

    /// <summary>Address bars (input scope URL) get compositions; see <see cref="_composeOnly"/>.</summary>
    private void LearnKind(ITfContext context, uint ec)
    {
        _kindKnown = true;
        if (RangeAtCaret(context, ec) is not { } range || context.GetAppProperty(TsfIds.PropInputScope, out var property) < 0
            || property is null)
        {
            if (Log.IsVerbose) Log.Info("no input scope property");
            return;
        }
        var value = default(ComVariant);
        try
        {
            var hr = property.GetValue(ec, range, &value);
            if (Log.IsVerbose) Log.Info($"input scope value 0x{hr:X8} {value.VarType}");
            if (hr < 0 || value.VarType != VarEnum.VT_UNKNOWN) return;
            var unknown = *(nint*)((byte*)&value + 8); // VARIANT.punkVal, after the type and three reserved words
            if (unknown == 0) return;
            var inputScope = ComInterfaceMarshaller<ITfInputScope>.ConvertToManaged((void*)unknown);
            int* scopes = null;
            uint count = 0;
            if (inputScope is null || inputScope.GetInputScopes(&scopes, &count) < 0 || scopes is null) return;
            try
            {
                var url = new ReadOnlySpan<int>(scopes, (int)count).Contains(TsfIds.IS_URL);
                if (Log.IsVerbose) Log.Info($"input scopes {string.Join(",", new ReadOnlySpan<int>(scopes, (int)count).ToArray())}");
                if (url) _composeOnly = true;
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)scopes);
            }
        }
        finally
        {
            value.Dispose();
        }
    }

    private void BackspaceInto(ITfContext context, uint ec)
    {
        if (RangeBeforeCaret(context, ec, _typed) is { } range)
        {
            _word.Backspace();
            Replace(context, ec, range, _word.Text);
            return;
        }
        // The caret moved off the word: this Backspace is an ordinary one.
        Forget();
        if (RangeAtCaret(context, ec) is not { } caret) return;
        if (caret.IsEmpty(ec, out var empty) >= 0 && empty != 0) caret.ShiftStart(ec, -1, out _, 0);
        caret.SetText(ec, 0, null, 0);
    }

    /// <summary>The range over the word before the caret, typed again into <see cref="_word"/>; null when there is none.</summary>
    private ITfRange? TakeBack(ITfContext context, uint ec, out string? text)
    {
        text = WordBeforeCaret(context, ec);
        if (text is null || Telex.Encode(text) is not { } keys || Telex.Transform(keys, _word.Options) != text
            || RangeBeforeCaret(context, ec, text) is not { } range)
        {
            text = null;
            return null;
        }
        foreach (var key in keys) _word.Type(key);
        _typed = text;
        if (Log.IsVerbose) Log.Info($"taking back '{text}'");
        return range;
    }

    /// <summary>
    /// Puts <paramref name="text"/> in place of <paramref name="range"/> and the caret after it; false when the app
    /// refused (nothing changed). An app that took the text but cannot give it back gets compositions from then on, as
    /// the next letter could not find the word again.
    /// </summary>
    private bool Replace(ITfContext context, uint ec, ITfRange range, string text)
    {
        fixed (char* chars = text)
        {
            if (range.SetText(ec, 0, chars, text.Length) < 0) return false;
        }
        if (text.Length > 0 && !HasText(range, ec, text))
        {
            if (Log.IsVerbose) Log.Info("the app does not give its text back: composing");
            _composeOnly = true;
            Forget();
        }
        else
        {
            _typed = text;
        }
        range.Collapse(ec, TsfIds.TF_ANCHOR_END);
        PutCaret(context, ec, range);
        return true;
    }

    private void Forget()
    {
        _word.Clear();
        _typed = "";
    }

    private void Commit(ITfContext context)
    {
        // Typed straight in, the word is already the app's.
        if (_composition is null)
        {
            Forget();
            return;
        }
        var text = _word.Text;
        Forget();
        Edit(context, ec => SetText(context, ec, text, end: true));
    }

    /// <summary>Commits where nothing may be edited right now (focus moving, being switched off).</summary>
    private void CommitLater()
    {
        if (_compositionContext is not { } context)
        {
            Forget();
            return;
        }
        var text = _word.Text;
        Forget();
        context.RequestEditSession(_clientId, new EditSession(ec => SetText(context, ec, text, end: true)),
            TsfIds.TF_ES_ASYNC | TsfIds.TF_ES_READWRITE, out _);
    }

    private void Edit(ITfContext context, Action<uint> edit, bool sync = false)
    {
        var session = new EditSession(edit);
        var hr = context.RequestEditSession(_clientId, session, TsfIds.TF_ES_SYNC | TsfIds.TF_ES_READWRITE, out var result);
        if (Log.IsVerbose && (hr < 0 || result < 0)) Log.Info($"edit session 0x{hr:X8}, result 0x{result:X8}");
        if ((hr >= 0 && result != TsfIds.TF_E_SYNCHRONOUS) || sync) return;
        // The app is busy with its text: TSF runs the edit as soon as it may.
        context.RequestEditSession(_clientId, session, TsfIds.TF_ES_ASYNC | TsfIds.TF_ES_READWRITE, out _);
    }

    private void SetText(ITfContext context, uint ec, string text, bool end, string? resumed = null)
    {
        if (_composition is null)
        {
            if (text.Length == 0) return;
            if (!StartComposition(context, ec, resumed))
            {
                // An app without compositions: give it the text as it is and start the next word afresh.
                fixed (char* chars = text) ((ITfInsertAtSelection)context).InsertTextAtSelection(ec, 0, (nint)chars, text.Length, out _);
                _word.Clear();
                return;
            }
        }
        var composition = _composition!;
        composition.GetRange(out var range);
        if (range is not null)
        {
            fixed (char* chars = text) range.SetText(ec, 0, chars, text.Length);
            if (end) ClearLook(context, ec, range);
            else SetLook(context, ec, range);
            range.Collapse(ec, TsfIds.TF_ANCHOR_END);
            PutCaret(context, ec, range);
        }
        if (!end) return;
        composition.EndComposition(ec);
        _composition = null;
        _compositionContext = null;
    }

    private bool StartComposition(ITfContext context, uint ec, string? resumed)
    {
        // Over the word being taken back, else an empty range at the caret (or the selected text, which the word then
        // types over).
        var range = resumed is null ? RangeAtCaret(context, ec) : RangeBeforeCaret(context, ec, resumed);
        if (range is null) return false;
        var hr = ((ITfContextComposition)context).StartComposition(ec, range, this, out var composition);
        if (composition is null)
        {
            Log.Info($"the app refused a composition: 0x{hr:X8}");
            return false;
        }
        _composition = composition;
        _compositionContext = context;
        return true;
    }

    private static ITfRange? RangeAtCaret(ITfContext context, uint ec)
    {
        ((ITfInsertAtSelection)context).InsertTextAtSelection(ec, TsfIds.TF_IAS_QUERYONLY, 0, 0, out var range);
        return range;
    }

    /// <summary>The letters right before the caret (up to <see cref="LongestWord"/>), or null when there are none.</summary>
    private static string? WordBeforeCaret(ITfContext context, uint ec)
    {
        // With text selected, the word types over the selection: nothing to take back.
        if (RangeAtCaret(context, ec) is not { } range || range.IsEmpty(ec, out var empty) < 0 || empty == 0) return null;
        if (range.ShiftStart(ec, -(LongestWord + 1), out _, 0) < 0) return null;
        var buffer = stackalloc char[LongestWord + 1];
        if (range.GetText(ec, 0, buffer, LongestWord + 1, out var length) < 0) return null;
        var before = new ReadOnlySpan<char>(buffer, (int)length);
        var start = before.Length;
        while (start > 0 && char.IsLetter(before[start - 1])) start--;
        var word = before[start..];
        // Too long to be one Vietnamese word (or longer than what was read): leave it.
        return word.IsEmpty || word.Length > LongestWord ? null : word.ToString();
    }

    /// <summary>
    /// When <paramref name="text"/> is what comes right before the caret, the range over it and over the selected text
    /// (which typing replaces, like the address bar's suggestion after the caret); else null.
    /// </summary>
    private static ITfRange? RangeBeforeCaret(ITfContext context, uint ec, string text)
    {
        if (RangeAtCaret(context, ec) is not { } range) return null;
        if (text.Length == 0) return range;
        if (range.ShiftStart(ec, -text.Length, out var moved, 0) < 0 || moved != -text.Length) return null;
        return Read(range, ec, text.Length) == text ? range : null;
    }

    private static bool HasText(ITfRange range, uint ec, string text) => Read(range, ec, text.Length + 1) == text;

    /// <summary>For the log: the text around the caret, the caret or selection marked with [ ].</summary>
    private static string Around(ITfContext context, uint ec)
    {
        if (RangeAtCaret(context, ec) is not { } range) return "?";
        var selected = Read(range, ec, 40);
        range.ShiftStart(ec, -20, out var moved, 0);
        var before = Read(range, ec, -moved);
        return $"{before}[{selected}]";
    }

    /// <summary>Up to <paramref name="max"/> characters from the start of <paramref name="range"/>.</summary>
    private static string? Read(ITfRange range, uint ec, int max)
    {
        var buffer = stackalloc char[max];
        return range.GetText(ec, 0, buffer, (uint)max, out var length) < 0 ? null : new string(buffer, 0, (int)length);
    }

    /// <summary>Marks the word with <see cref="DisplayAttribute"/>, so the app shows it without an underline.</summary>
    private void SetLook(ITfContext context, uint ec, ITfRange range)
    {
        if (_displayAttribute == 0 || context.GetProperty(TsfIds.PropAttribute, out var property) < 0 || property is null)
        {
            if (Log.IsVerbose) Log.Info($"no display attribute ({_displayAttribute})");
            return;
        }
        var value = ComVariant.Create((int)_displayAttribute);
        var hr = property.SetValue(ec, range, &value);
        if (Log.IsVerbose) Log.Info($"display attribute {_displayAttribute}: 0x{hr:X8}");
    }

    private static void ClearLook(ITfContext context, uint ec, ITfRange range)
    {
        if (context.GetProperty(TsfIds.PropAttribute, out var property) < 0 || property is null) return;
        property.Clear(ec, range);
    }

    private static void PutCaret(ITfContext context, uint ec, ITfRange range)
    {
        var pointer = ComInterfaceMarshaller<ITfRange>.ConvertToUnmanaged(range);
        try
        {
            context.SetSelection(ec, 1, new TfSelection { Range = (nint)pointer });
        }
        finally
        {
            ComInterfaceMarshaller<ITfRange>.Free(pointer);
        }
    }

    private static uint Advise(object source, object sink, Guid sinkInterface)
    {
        Check(Exports.QueryInterface(sink, sinkInterface, out var pointer), "QueryInterface");
        try
        {
            Check(((ITfSource)source).AdviseSink(sinkInterface, pointer, out var cookie), "AdviseSink");
            return cookie;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(pointer);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what}: 0x{hr:X8}");
    }
}
