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
    Task Pause(int milliseconds, CancellationToken ct);
}

/// <summary>The real keyboard: SendInput into the foreground window.</summary>
public sealed class WindowsPromptKeyboard(IWindowService windows) : IPromptKeyboard
{
    private const ushort Shift = 0x10, Control = 0x11, Return = 0x0D;

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

    public Task Pause(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
}

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

    /// <summary>Types <paramref name="text"/> into <paramref name="sessionId"/> (null: a new session) in that window.</summary>
    /// <param name="model">New session only: sent as <c>/model …</c> first.</param>
    /// <exception cref="PromptSendException">Nothing or only part was typed; the message says why.</exception>
    public async Task SendAsync(BridgeConnection window, string? sessionId, string text, string? model, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var previous = keyboard.Foreground();
        nint target = 0;
        try
        {
            if (sessionId is null) await Bridge(window, "new", null, ct).ConfigureAwait(false);
            else await Bridge(window, "open", sessionId, ct).ConfigureAwait(false);
            await keyboard.Pause(400, ct).ConfigureAwait(false);

            target = VsCodeWindows.Find(keyboard.TopWindows(), window.Info.Name);
            if (target == 0) throw new PromptSendException($"The VS Code window “{window.Info.Name}” was not found (or two windows have that name).");

            if (!string.IsNullOrWhiteSpace(model) && sessionId is null)
            {
                await TypeAndSubmitAsync(window, target, "/model " + model.Trim(), ct).ConfigureAwait(false);
                await keyboard.Pause(1500, ct).ConfigureAwait(false);
            }
            await TypeAndSubmitAsync(window, target, text, ct).ConfigureAwait(false);
        }
        finally
        {
            if (previous != 0 && previous != target) keyboard.Activate(previous);
            _gate.Release();
        }
    }

    private async Task TypeAndSubmitAsync(BridgeConnection window, nint target, string text, CancellationToken ct)
    {
        if (!keyboard.ForceActivate(target)) throw new PromptSendException("Windows did not let Helm bring VS Code to the front. Nothing was typed.");
        await Bridge(window, "focus", null, ct).ConfigureAwait(false);
        await keyboard.Pause(300, ct).ConfigureAwait(false);

        var lines = SessionRegistry.Normalize(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            Guard(target, i == 0 ? "Nothing was typed." : "Part of the prompt was typed; nothing was sent.");
            if (i > 0) keyboard.NewLine();
            if (lines[i].Length > 0) keyboard.Type(lines[i]);
        }
        await keyboard.Pause(150, ct).ConfigureAwait(false);
        Guard(target, "The prompt was typed but not sent.");
        keyboard.Submit(window.Info.CtrlEnterSends);
        logger.LogInformation("Workspace: typed a prompt ({Length} chars) into VS Code window {Name}", text.Length, window.Info.Name);
    }

    /// <summary>Stops before any key goes to another window, or turns into a shortcut because a modifier is held.</summary>
    private void Guard(nint target, string state)
    {
        if (keyboard.Foreground() != target) throw new PromptSendException("Another window came to the front. " + state);
        if (keyboard.ModifierDown()) throw new PromptSendException("A key (Ctrl, Alt, Shift or Win) is held down on the PC. " + state);
    }

    private static async Task Bridge(BridgeConnection window, string op, string? session, CancellationToken ct)
    {
        try
        {
            await window.RequestAsync(op, session, null, ct).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            throw new PromptSendException(ex.Message);
        }
    }
}
