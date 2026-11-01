using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>The keyboard and the windows, as <see cref="PromptSender"/> needs them (a fake in tests).</summary>
public interface IPromptKeyboard
{
    IReadOnlyList<(nint Handle, string Title)> TopWindows();
    nint Foreground();
    bool ForceActivate(nint hwnd);
    void Activate(nint hwnd);
    bool ModifierDown();
    void Type(string text);
    void NewLine();
    void Submit(bool ctrl);

    /// <summary>Shift+Tab: the next permission mode in Claude's input.</summary>
    void NextMode() { }

    Task Pause(int milliseconds, CancellationToken ct);

    /// <summary>True when <paramref name="task"/> completes within <paramref name="milliseconds"/>.</summary>
    Task<bool> WaitFor(Task task, int milliseconds, CancellationToken ct);
}

/// <summary>The real keyboard: SendInput into the foreground window.</summary>
public sealed class WindowsPromptKeyboard(IWindowService windows) : IPromptKeyboard
{
    private const ushort Shift = 0x10, Control = 0x11, Return = 0x0D, Tab = 0x09;

    public IReadOnlyList<(nint Handle, string Title)> TopWindows() =>
        windows.GetAppWindows().Where(w => w.ProcessName.Equals("Code.exe", StringComparison.OrdinalIgnoreCase) || w.ProcessName.Equals("Code - Insiders.exe", StringComparison.OrdinalIgnoreCase))
            .Select(w => (w.Handle, w.Title)).ToList();

    public nint Foreground() => windows.GetForegroundWindow();
    public bool ForceActivate(nint hwnd) => windows.ForceActivate(hwnd);
    public void Activate(nint hwnd) => windows.Activate(hwnd);
    public bool ModifierDown() => InputSimulator.AnyModifierDown();
    public void Type(string text) => InputSimulator.TypeText(text);
    public void NewLine() => InputSimulator.PressChord(Shift, Return);
    public void Submit(bool ctrl)
    {
        if (ctrl) InputSimulator.PressChord(Control, Return);
        else InputSimulator.PressKey(Return);
    }

    public void NextMode() => InputSimulator.PressChord(Shift, Tab);

    public Task Pause(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);

    public async Task<bool> WaitFor(Task task, int milliseconds, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(milliseconds), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>What a new session is set to before its first prompt: model and effort (slash commands), mode (Shift+Tab).</summary>
public sealed record NewSessionSetup(string? Model, string? Effort, int ModePresses);

/// <summary>Typing into a VS Code window failed; the message says why, in words for the phone.</summary>
public sealed class PromptSendException(string message) : Exception(message);

/// <summary>
/// Types a prompt into a Claude Code session in VS Code, the way the S0 spike proved works: Helm Bridge switches to
/// the session's tab, Helm brings the window to the front, Bridge focuses Claude's input, Helm types the text
/// (Shift+Enter between lines) and presses Enter. Keys are only ever sent while that VS Code window really is the
/// foreground window, and never while you hold a modifier key. One prompt at a time; afterwards the window you were
/// in comes back to the front.
/// </summary>
public sealed class PromptSender(IPromptKeyboard keyboard, ILogger<PromptSender> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _typedUntil;

    /// <summary>
    /// Until when the keyboard and mouse input of this PC may be Helm's own typing (<see cref="Environment.TickCount64"/>
    /// clock, a little after the last key Helm sent). Input after it is someone at the PC.
    /// </summary>
    public long TypedUntilTick => Interlocked.Read(ref _typedUntil);

    /// <summary>How long one Enter may take to show up as Claude's UserPromptSubmit before Enter is pressed again.</summary>
    public const int ConfirmMilliseconds = 4000;

    /// <summary>Enter is pressed at most this many more times when Claude did not take the prompt.</summary>
    public const int SubmitRetries = 2;

    /// <summary>Types <paramref name="text"/> into <paramref name="sessionId"/> (null: a new session) in that window.</summary>
    /// <param name="setup">New session only: model, effort and mode, set before the prompt.</param>
    /// <param name="received">
    /// Completes when Claude confirmed the prompt (its UserPromptSubmit hook). Until then Enter is pressed again a
    /// couple of times: an Enter that landed while Claude's input was busy is lost, and Enter in an empty input (the
    /// prompt went through after all) does nothing, so a retry never sends twice.
    /// </param>
    /// <exception cref="PromptSendException">Nothing or only part was typed; the message says why.</exception>
    /// <param name="step">Told each step as it starts, in words for the phone ("Typing your prompt").</param>
    /// <param name="tab">
    /// The name of the editor tab showing the session, when Helm knows it (null: in the side bar, or not told apart).
    /// Keys only go in while that tab has the keyboard.
    /// </param>
    public async Task SendAsync(BridgeConnection window, string? sessionId, string text, NewSessionSetup? setup, CancellationToken ct, Task? received = null,
        Action<string>? step = null, string? tab = null)
    {
        if (sessionId is null) tab = null;
        step ??= _ => { };
        if (_gate.CurrentCount == 0) step("Waiting for the prompt before it on this PC");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var previous = keyboard.Foreground();
        nint target = 0;
        try
        {
            step(sessionId is null ? $"Opening a new Claude tab in {window.Info.Name}" : $"Switching to the tab in {window.Info.Name}");
            var editor = sessionId is null
                ? await Bridge(window, "new", null, ct).ConfigureAwait(false)
                : await Bridge(window, "open", sessionId, ct).ConfigureAwait(false);
            await keyboard.Pause(400, ct).ConfigureAwait(false);

            target = FindWindow(window, editor, "Nothing was typed.");

            // A new tab is the active editor but not yet Claude's "current chat"; an open session (in an editor tab
            // or in the side bar) was just revealed, and Claude's own focus command goes to it.
            var focus = sessionId is null ? "focusEditor" : "focus";
            if (sessionId is null && setup is not null)
            {
                if (!string.IsNullOrWhiteSpace(setup.Model))
                {
                    step($"Setting the model to {setup.Model.Trim()}");
                    await TypeAndSubmitAsync(window, target, focus, null, null, "/model " + setup.Model.Trim(), ct).ConfigureAwait(false);
                    await keyboard.Pause(2500, ct).ConfigureAwait(false);
                }
                if (!string.IsNullOrWhiteSpace(setup.Effort))
                {
                    step($"Setting the effort to {setup.Effort.Trim()}");
                    await TypeAndSubmitAsync(window, target, focus, null, null, "/effort " + setup.Effort.Trim(), ct).ConfigureAwait(false);
                    await keyboard.Pause(1500, ct).ConfigureAwait(false);
                }
                if (setup.ModePresses > 0)
                {
                    step("Setting the permission mode");
                    if (!keyboard.ForceActivate(target)) throw new PromptSendException("Windows did not let Helm bring VS Code to the front. Nothing was typed.");
                    await ConfirmFocusAsync(window, "Nothing was typed.", ct).ConfigureAwait(false);
                    await Bridge(window, focus, null, ct).ConfigureAwait(false);
                    await keyboard.Pause(300, ct).ConfigureAwait(false);
                    for (var i = 0; i < setup.ModePresses; i++)
                    {
                        Guard(window, target, null, "The mode was not set; nothing was typed.");
                        keyboard.NextMode();
                        await keyboard.Pause(150, ct).ConfigureAwait(false);
                    }
                }
            }
            step("Typing your prompt into VS Code");
            await TypeAndSubmitAsync(window, target, focus, sessionId, tab, text, ct).ConfigureAwait(false);
            step("Waiting for Claude to take it");
            if (received is not null) await ConfirmAsync(window, target, focus, sessionId, tab, received, ct).ConfigureAwait(false);
        }
        finally
        {
            if (previous != 0 && previous != target) keyboard.Activate(previous);
            Interlocked.Exchange(ref _typedUntil, Environment.TickCount64 + 1500);
            _gate.Release();
        }
    }

    /// <summary>
    /// Presses Shift+Tab <paramref name="presses"/> times in an open session's input: each press is the next permission
    /// mode. Same guards as typing: only into that window, never with a modifier held.
    /// </summary>
    /// <exception cref="PromptSendException">Nothing or only part was pressed; the message says why.</exception>
    public async Task CycleModeAsync(BridgeConnection window, string sessionId, int presses, CancellationToken ct, string? tab = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var previous = keyboard.Foreground();
        nint target = 0;
        try
        {
            var editor = await Bridge(window, "open", sessionId, ct).ConfigureAwait(false);
            await keyboard.Pause(400, ct).ConfigureAwait(false);
            target = FindWindow(window, editor, "The mode did not change.");
            if (!keyboard.ForceActivate(target)) throw new PromptSendException("Windows did not let Helm bring VS Code to the front. The mode did not change.");
            await ConfirmFocusAsync(window, "The mode did not change.", ct).ConfigureAwait(false);
            await FocusInputAsync(window, "focus", sessionId, tab, ct).ConfigureAwait(false);
            await keyboard.Pause(300, ct).ConfigureAwait(false);
            for (var i = 0; i < presses; i++)
            {
                Guard(window, target, tab, i == 0 ? "The mode did not change." : "The mode changed only part of the way.");
                keyboard.NextMode();
                await keyboard.Pause(150, ct).ConfigureAwait(false);
            }
            logger.LogInformation("Workspace: pressed Shift+Tab {Presses} times in VS Code window {Name}", presses, window.Info.Name);
        }
        finally
        {
            if (previous != 0 && previous != target) keyboard.Activate(previous);
            Interlocked.Exchange(ref _typedUntil, Environment.TickCount64 + 1500);
            _gate.Release();
        }
    }

    private async Task TypeAndSubmitAsync(BridgeConnection window, nint target, string focus, string? sessionId, string? tab, string text, CancellationToken ct)
    {
        if (!keyboard.ForceActivate(target)) throw new PromptSendException("Windows did not let Helm bring VS Code to the front. Nothing was typed.");
        await ConfirmFocusAsync(window, "Nothing was typed.", ct).ConfigureAwait(false);
        await FocusInputAsync(window, focus, sessionId, tab, ct).ConfigureAwait(false);
        await keyboard.Pause(300, ct).ConfigureAwait(false);

        var lines = SessionRegistry.Normalize(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            Guard(window, target, tab, i == 0 ? "Nothing was typed." : "Part of the prompt was typed; nothing was sent.");
            if (i > 0) keyboard.NewLine();
            if (lines[i].Length > 0) keyboard.Type(lines[i]);
        }
        await keyboard.Pause(400, ct).ConfigureAwait(false);
        Guard(window, target, tab, "The prompt was typed but not sent.");
        keyboard.Submit(window.Info.CtrlEnterSends);
        Interlocked.Exchange(ref _typedUntil, Environment.TickCount64 + 1500);
        logger.LogInformation("Workspace: typed a prompt ({Length} chars) into VS Code window {Name}", text.Length, window.Info.Name);
    }

    private async Task ConfirmAsync(BridgeConnection window, nint target, string focus, string? sessionId, string? tab, Task received, CancellationToken ct)
    {
        for (var attempt = 0; attempt < SubmitRetries; attempt++)
        {
            if (await keyboard.WaitFor(received, ConfirmMilliseconds, ct).ConfigureAwait(false)) return;
            // Back to the same tab and input, then Enter once more.
            if (!keyboard.ForceActivate(target)) return;
            await FocusInputAsync(window, focus, sessionId, tab, ct).ConfigureAwait(false);
            await keyboard.Pause(300, ct).ConfigureAwait(false);
            if (keyboard.Foreground() != target || !window.Info.Focused || !HasKeyboard(window, tab) || keyboard.ModifierDown()) return;
            keyboard.Submit(window.Info.CtrlEnterSends);
            logger.LogInformation("Workspace: Claude had not taken the prompt in {Name}; pressed Enter again", window.Info.Name);
        }
    }

    /// <summary>
    /// Stops before any key goes to another window, or turns into a shortcut because a modifier is held. The window in
    /// front must be the one found by its title AND the bridge inside it must say it has the keyboard: a title alone
    /// can belong to another VS Code window with the same name.
    /// </summary>
    private void Guard(BridgeConnection window, nint target, string? tab, string state)
    {
        if (keyboard.Foreground() != target || !window.Info.Focused) throw new PromptSendException("Another window came to the front. " + state);
        if (!HasKeyboard(window, tab)) throw new PromptSendException($"Another Claude tab in VS Code has the keyboard, not “{tab}”. " + state);
        if (keyboard.ModifierDown()) throw new PromptSendException("A key (Ctrl, Alt, Shift or Win) is held down on the PC. " + state);
    }

    /// <summary>
    /// True when the session's editor tab is the one in front of the active group, as the bridge last reported (always
    /// true when Helm does not know the tab, or an older bridge does not list tabs).
    /// </summary>
    private static bool HasKeyboard(BridgeConnection window, string? tab) =>
        tab is null || window.Info.Tabs is not { } tabs || tabs.Any(t => t.Active && t.Label == tab);

    /// <summary>
    /// Puts the keyboard in Claude's input. Claude's own focus command focuses the input of every Claude panel in view,
    /// so with two side by side the keyboard could end up in the other one. For a session in an editor tab Helm then
    /// shows that tab again and focuses its group: the keyboard goes back into that tab, where the input is focused.
    /// </summary>
    private static async Task FocusInputAsync(BridgeConnection window, string focus, string? sessionId, string? tab, CancellationToken ct)
    {
        await Bridge(window, focus, null, ct).ConfigureAwait(false);
        if (focus != "focus" || sessionId is null || tab is null) return;
        await Bridge(window, "open", sessionId, ct).ConfigureAwait(false);
        await Bridge(window, "focusEditor", null, ct).ConfigureAwait(false);
    }

    /// <summary>How long the bridge may take to report that its window got the keyboard after Helm raised it.</summary>
    public const int FocusConfirmMilliseconds = 2000;

    /// <summary>
    /// Waits until Helm Bridge in <paramref name="window"/> reports that its VS Code window has the keyboard (VS Code's
    /// own window state). Nothing is ever pressed before that: the window Helm raised might be another one whose title
    /// only looks the same.
    /// </summary>
    private async Task ConfirmFocusAsync(BridgeConnection window, string state, CancellationToken ct)
    {
        for (var waited = 0; !window.Info.Focused; waited += 50)
        {
            if (waited >= FocusConfirmMilliseconds)
                throw new PromptSendException($"The VS Code window “{window.Info.Name}” did not confirm it has the keyboard. " + state);
            await keyboard.Pause(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The top-level window showing <paramref name="editor"/> in <paramref name="window"/>'s workspace.</summary>
    private nint FindWindow(BridgeConnection window, string? editor, string state)
    {
        var windows = keyboard.TopWindows();
        var target = VsCodeWindows.Find(windows, window.Info.Name, editor);
        if (target != 0) return target;
        throw new PromptSendException(windows.Any(w => VsCodeWindows.Matches(w.Title, window.Info.Name))
            ? $"Several VS Code windows show “{window.Info.Name}” and Helm could not tell which one holds the Claude tab. {state}"
            : $"The VS Code window “{window.Info.Name}” was not found. {state}");
    }

    /// <returns>The tab in front after the request (null from an older bridge).</returns>
    private static async Task<string?> Bridge(BridgeConnection window, string op, string? session, CancellationToken ct)
    {
        try
        {
            return await window.RequestAsync(op, session, null, ct).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            throw new PromptSendException(ex.Message);
        }
    }
}
