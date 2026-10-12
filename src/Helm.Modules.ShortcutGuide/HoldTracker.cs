using Helm.Core.Hotkeys;

namespace Helm.Modules.ShortcutGuide;

/// <summary>What the engine should do after a key or mouse event.</summary>
public enum HoldAction
{
    None,

    /// <summary>The held modifiers changed and nothing else was pressed: (re)start the hold timer.</summary>
    Arm,

    /// <summary>The guide is showing and the held modifiers changed: show the shortcuts for the new set.</summary>
    Update,

    /// <summary>Hide the guide: a key or the mouse was used, or every modifier was released.</summary>
    Hide,
}

/// <summary>
/// Follows the keyboard to tell when modifiers are held <em>alone</em>: the guide appears only after Ctrl, Alt, Shift
/// or Win (or several) stayed down for a while with nothing else pressed. Pressing any other key or a mouse button
/// means you are using a shortcut, so the guide stays away until every modifier is released. Not thread-safe; the
/// engine locks around it. Pure logic, no Win32, so it is unit-tested.
/// </summary>
public sealed class HoldTracker
{
    /// <summary>
    /// Autorepeat sends a key down about 30 times a second after a delay of at most one second (Windows' longest
    /// setting); a longer silence means the release was missed.
    /// </summary>
    private const uint RepeatGapMs = 1500;

    private readonly Dictionary<int, uint> _down = new(); // virtual key → time of its last key down
    private readonly Func<int, bool>? _isPhysicallyDown;

    /// <param name="isPhysicallyDown">
    /// Asks Windows whether a key is really down (GetAsyncKeyState). Releases can be missed (Win+L locks the PC and
    /// the Win key goes up on the lock screen, where hooks see nothing); this drops such stuck keys.
    /// </param>
    public HoldTracker(Func<int, bool>? isPhysicallyDown = null) => _isPhysicallyDown = isPhysicallyDown;

    public HotkeyModifiers Held { get; private set; }

    /// <summary>Something besides modifiers was pressed during this hold.</summary>
    public bool IsSpoiled { get; private set; }

    public bool IsShowing { get; private set; }

    /// <summary>Changes whenever a pending hold timer must no longer fire.</summary>
    public int Generation { get; private set; }

    public HoldAction OnKey(int virtualKey, bool isDown, uint time)
    {
        var modifier = ToModifier(virtualKey);
        if (modifier == HotkeyModifiers.None) return isDown ? OnOtherInput() : HoldAction.None;

        if (isDown)
        {
            if (_down.TryGetValue(virtualKey, out var last))
            {
                _down[virtualKey] = time;
                if (unchecked(time - last) < RepeatGapMs || IsShowing) return HoldAction.None; // autorepeat while held
                _down.Remove(virtualKey); // its release was missed: this is a new press
            }
            DropStuckKeys(except: 0);
            // Nothing else is really down: a new hold starts, whatever happened during the last one.
            if (_down.Count == 0) IsSpoiled = false;
            _down[virtualKey] = time;
            return OnModifiersChanged();
        }

        if (!_down.Remove(virtualKey)) return HoldAction.None;
        return OnModifiersChanged();
    }

    /// <summary>A mouse button or the wheel: Ctrl+click, Shift+click and Ctrl+wheel are shortcuts too.</summary>
    public HoldAction OnMouseButton() => OnOtherInput();

    /// <summary>The hold timer of <paramref name="generation"/> fired: true when the guide should appear now.</summary>
    public bool ShouldShow(int generation)
    {
        if (generation != Generation || IsSpoiled || IsShowing) return false;
        DropStuckKeys(except: 0);
        Held = Compute();
        return Held != HotkeyModifiers.None;
    }

    /// <summary>The guide is on screen.</summary>
    public void MarkShown() => IsShowing = true;

    /// <summary>
    /// Safety net while the guide shows: if Windows says no modifier is down any more, forget them all. Returns true
    /// when the guide must hide.
    /// </summary>
    public bool CheckReleased()
    {
        DropStuckKeys(except: 0);
        if (_down.Count > 0) return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        _down.Clear();
        Held = HotkeyModifiers.None;
        IsSpoiled = false;
        IsShowing = false;
        Generation++;
    }

    private HoldAction OnModifiersChanged()
    {
        Held = Compute();
        if (Held == HotkeyModifiers.None)
        {
            var wasShowing = IsShowing;
            Reset();
            return wasShowing ? HoldAction.Hide : HoldAction.None;
        }
        if (IsShowing) return HoldAction.Update;
        if (IsSpoiled) return HoldAction.None;
        Generation++;
        return HoldAction.Arm;
    }

    private HoldAction OnOtherInput()
    {
        if (Held == HotkeyModifiers.None) return HoldAction.None;
        IsSpoiled = true;
        Generation++;
        if (!IsShowing) return HoldAction.None;
        IsShowing = false;
        return HoldAction.Hide;
    }

    private void DropStuckKeys(int except)
    {
        if (_isPhysicallyDown is null) return;
        foreach (var vk in _down.Keys.Where(vk => vk != except && !_isPhysicallyDown(vk)).ToList())
            _down.Remove(vk);
    }

    private HotkeyModifiers Compute()
    {
        var held = HotkeyModifiers.None;
        foreach (var vk in _down.Keys) held |= ToModifier(vk);
        return held;
    }

    public static HotkeyModifiers ToModifier(int virtualKey) => virtualKey switch
    {
        VirtualKeyNames.Control or VirtualKeyNames.LControl or VirtualKeyNames.RControl => HotkeyModifiers.Ctrl,
        VirtualKeyNames.Menu or VirtualKeyNames.LMenu or VirtualKeyNames.RMenu => HotkeyModifiers.Alt,
        VirtualKeyNames.Shift or VirtualKeyNames.LShift or VirtualKeyNames.RShift => HotkeyModifiers.Shift,
        VirtualKeyNames.LWin or VirtualKeyNames.RWin => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };
}
