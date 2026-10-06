using Android.Content;
using Android.Runtime;
using Android.Views;
using Android.Views.InputMethods;
using Android.Webkit;

namespace Helm.Modules.Ssh.Terminal;

/// <summary>
/// The terminal's WebView, with the keyboard's edits turned into what a terminal sends. A keyboard app does not send
/// Enter and Backspace as keys: it asks the text field to run its action and to delete text around the cursor. A
/// terminal has no local text to delete, so those become Enter (CR) and Delete (DEL) for the server, as in Termux.
/// Typed text keeps the keyboard's own path, so composing (Vietnamese Telex and the like) still works.
/// </summary>
internal sealed class TerminalWebView : WebView
{
    private readonly Action<string> _send;
    private readonly Func<KeyEvent, bool> _key;

    public TerminalWebView(Context context, Action<string> send, Func<KeyEvent, bool> key)
        : base(context)
    {
        _send = send;
        _key = key;
    }

    // Needed by the Android runtime when it recreates the managed peer.
    private TerminalWebView(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
        _send = _ => { };
        _key = _ => false;
    }

    /// <summary>
    /// WebView handles keys itself and never calls a key listener, so the terminal's keys are taken here, before it.
    /// </summary>
    public override bool DispatchKeyEvent(KeyEvent? e) => e is not null && _key(e) || base.DispatchKeyEvent(e);

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        var inner = base.OnCreateInputConnection(outAttrs);
        if (outAttrs is not null)
        {
            // No full-screen editing box in landscape, and no "Next"/"Done" in place of Enter.
            outAttrs.ImeOptions |= ImeFlags.NoExtractUi | ImeFlags.NoFullscreen;
            outAttrs.ImeOptions = (ImeFlags)(((int)outAttrs.ImeOptions & ~(int)ImeAction.ImeMaskAction) | (int)ImeAction.None);
        }
        return inner is null ? null : new Connection(inner, this);
    }

    private sealed class Connection(IInputConnection inner, TerminalWebView view) : InputConnectionWrapper(inner, true)
    {
        public override bool PerformEditorAction(ImeAction editorAction)
        {
            view._send("\r");
            return true;
        }

        public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        {
            if (beforeLength > 0) view._send(new string('\u007f', Math.Min(beforeLength, 64)));
            return true;
        }

        public override bool SendKeyEvent(KeyEvent? e) => e is not null && view._key(e) || base.SendKeyEvent(e);
    }
}
