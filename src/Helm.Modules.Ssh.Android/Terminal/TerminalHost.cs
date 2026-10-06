using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Android.Content;
using Android.Views.InputMethods;
using Android.Webkit;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Helm.Core.Platform;
using Helm.Shell.Services;
using AndroidColor = Android.Graphics.Color;

namespace Helm.Modules.Ssh.Terminal;

/// <summary>
/// The terminal of the selected server's session on Android: the same xterm.js page as on Windows, in Android's
/// WebView. The page's files are served from this assembly (never written to disk, nothing loaded from the network),
/// every other request is refused, no navigation leaves the page, and the page and Helm talk through a MessagePort
/// that Helm hands to the page's own origin (no JavaScript interface is exposed). A WebView lives only while the page
/// is shown; switching servers or coming back redraws from the session's buffer. UI thread only.
/// </summary>
internal sealed class TerminalHost : NativeControlHost
{
    private const string Origin = "https://helm-ssh.local/";
    private const string PageUrl = Origin + "terminal.html";

    private static readonly Dictionary<string, (string Resource, string Mime)> Files = new(StringComparer.Ordinal)
    {
        ["terminal.html"] = ("Helm.Ssh.terminal.html", "text/html"),
        ["terminal.js"] = ("Helm.Ssh.terminal.js", "text/javascript"),
        ["xterm.js"] = ("Helm.Ssh.xterm.js", "text/javascript"),
        ["xterm.css"] = ("Helm.Ssh.xterm.css", "text/css"),
        ["addon-fit.js"] = ("Helm.Ssh.addon-fit.js", "text/javascript"),
    };

    private readonly SshViewModel _viewModel;
    private readonly IClipboardService _clipboard;
    private readonly ConcurrentQueue<(int Generation, byte[] Bytes)> _pending = new();
    private WebView? _web;
    private WebMessagePort? _port;
    private bool _ready;
    private SshSession? _attached;
    private Action<byte[]>? _sink;
    private int _generation;
    private int _flushQueued;
    // Keys whose press was seen: some keyboards keep a press for themselves and let only the release through.
    private readonly HashSet<global::Android.Views.Keycode> _pressed = [];

    public TerminalHost(SshViewModel viewModel, IClipboardService clipboard)
    {
        _viewModel = viewModel;
        _clipboard = clipboard;
    }

    /// <summary>A key from the key bar (esc, tab, up, down, left, right, home, end); the page sends it as the terminal would.</summary>
    public void SendKey(string key) => Post(new { t = "key", k = key });

    /// <summary>Shows the keyboard and puts the typing focus in the terminal.</summary>
    public void FocusTerminal()
    {
        if (_web is not { } web) return;
        web.RequestFocus();
        Post(new { t = "focus" });
        if (web.Context?.GetSystemService(Context.InputMethodService) is InputMethodManager keyboard) keyboard.ShowSoftInput(web, ShowFlags.Implicit);
    }

    /// <summary>Pastes the clipboard's text, confirming text with line breaks first (see <see cref="TerminalPaste"/>).</summary>
    public async Task PasteAsync(bool bracketed = false)
    {
        string text;
        try
        {
            var clipboard = ActivityHost.Latest?.GetSystemService(Context.ClipboardService) as ClipboardManager;
            text = clipboard?.PrimaryClip is { ItemCount: > 0 } clip ? clip.GetItemAt(0)?.CoerceToText(ActivityHost.Latest)?.ToString() ?? "" : "";
        }
        catch (Java.Lang.SecurityException)
        {
            return;
        }
        if (text.Length == 0 || _viewModel.ActiveSession?.IsConnected != true) return;
        if (TerminalPaste.NeedsConfirmation(text, bracketed)
            && !await _viewModel.AskAsync("Paste text with line breaks?", TerminalPaste.Warning(text), "Paste").ConfigureAwait(true)) return;
        Post(new { t = "paste", d = TerminalPaste.ForTerminal(text) });
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var context = (Context?)ActivityHost.Latest ?? global::Android.App.Application.Context;
        var web = new TerminalWebView(context, text => _viewModel.Send(text), e => HandleKey(e.KeyCode, e));
        var settings = web.Settings;
        settings.JavaScriptEnabled = true;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.DomStorageEnabled = false;
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.SetSupportMultipleWindows(false);
        settings.SetGeolocationEnabled(false);
        settings.CacheMode = CacheModes.NoCache;
        settings.MediaPlaybackRequiresUserGesture = true;
        settings.SetSupportZoom(false);
        settings.BuiltInZoomControls = false;
        web.SetBackgroundColor(AndroidColor.Rgb(0x0C, 0x0C, 0x0C));
        web.SetWebViewClient(new Client(this));
        web.Focusable = true;
        web.FocusableInTouchMode = true;
        web.LoadUrl(PageUrl);
        _web = web;
        _viewModel.PropertyChanged += OnViewModelChanged;
        return new AndroidViewControlHandle(web);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        Detach();
        _ready = false;
        var port = _port;
        var web = _web;
        _port = null;
        _web = null;
        // Avalonia may already have released the Java objects when the page left the screen: each step is best effort.
        Quietly(() => port?.Close());
        Quietly(() =>
        {
            if (web is null || web.Handle == IntPtr.Zero) return;
            web.StopLoading();
            web.Destroy();
        });
        Quietly(() => base.DestroyNativeControlCore(control));
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SshViewModel.ActiveSession)) Rebind();
        else if (e.PropertyName == nameof(SshViewModel.FontSize)) Post(new { t = "font", s = _viewModel.FontSize });
    }

    /// <summary>The page has loaded: it gets its end of a private channel, sent only to its own origin.</summary>
    private void OnPageLoaded(WebView view)
    {
        if (_web != view || _port is not null) return;
        var ports = view.CreateWebMessageChannel();
        _port = ports[0];
        _port.SetWebMessageCallback(new Callback(this));
        view.PostWebMessage(new WebMessage("helm-port", [ports[1]]), global::Android.Net.Uri.Parse(Origin.TrimEnd('/'))!);
    }

    private void OnMessage(string? json)
    {
        if (json is null) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "ready":
                    _ready = true;
                    Post(new { t = "font", s = _viewModel.FontSize });
                    _viewModel.ReportTerminalSize(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                    Rebind();
                    break;
                case "in":
                    _viewModel.Send(root.GetProperty("d").GetString() ?? "");
                    break;
                case "bin":
                    _viewModel.ActiveSession?.Send(Encoding.Latin1.GetBytes(root.GetProperty("d").GetString() ?? ""));
                    break;
                case "size":
                    _viewModel.ReportTerminalSize(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                    break;
                case "copy":
                    if (root.GetProperty("d").GetString() is { Length: > 0 } text) _clipboard.SetText(text);
                    break;
                case "paste":
                    _ = PasteAsync(root.TryGetProperty("b", out var b) && b.ValueKind == JsonValueKind.True);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Not a message of ours: ignored.
        }
    }

    /// <summary>Shows the selected server's session: redraws from its buffer and takes its output from now on.</summary>
    private void Rebind()
    {
        Detach();
        var generation = Interlocked.Increment(ref _generation);
        if (!_ready) return;
        if (_viewModel.ActiveSession is not { } session)
        {
            Post(new { t = "reset", d = "" });
            return;
        }
        Action<byte[]> sink = bytes =>
        {
            _pending.Enqueue((generation, bytes));
            if (Interlocked.Exchange(ref _flushQueued, 1) == 0) Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        };
        var snapshot = session.Attach(sink);
        _attached = session;
        _sink = sink;
        Post(new { t = "reset", d = Convert.ToBase64String(snapshot) });
    }

    private void Detach()
    {
        if (_attached is { } old && _sink is { } sink) old.Detach(sink);
        _attached = null;
        _sink = null;
        Interlocked.Increment(ref _generation);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        var current = Volatile.Read(ref _generation);
        using var batch = new MemoryStream();
        while (_pending.TryDequeue(out var item))
        {
            if (item.Generation == current) batch.Write(item.Bytes);
        }
        if (batch.Length > 0) Post(new { t = "out", d = Convert.ToBase64String(batch.GetBuffer(), 0, (int)batch.Length) });
    }

    private void Post(object message)
    {
        if (!_ready || _port is not { } port) return;
        try
        {
            port.PostMessage(new WebMessage(JsonSerializer.Serialize(message)));
        }
        catch (Java.Lang.IllegalStateException)
        {
            // The page went away.
        }
    }

    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Java.Lang.IllegalStateException or Java.Lang.Throwable)
        {
            // Already released.
        }
    }

    private static Stream? Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    private sealed class Client(TerminalHost host) : WebViewClient
    {
        /// <summary>Serves the page's own files; anything else (any other host or path) gets a 403.</summary>
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString() ?? "";
            if (url.StartsWith(Origin, StringComparison.Ordinal) && Files.TryGetValue(url[Origin.Length..], out var file) && Resource(file.Resource) is { } stream)
                return new WebResourceResponse(file.Mime, "utf-8", 200, "OK", new Dictionary<string, string> { ["Cache-Control"] = "no-store" }, stream);
            return new WebResourceResponse("text/plain", "utf-8", 403, "Forbidden", new Dictionary<string, string>(), new MemoryStream());
        }

        /// <summary>The page never leaves: links and redirects go nowhere.</summary>
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => request?.Url?.ToString() != PageUrl;

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (view is not null && url == PageUrl) host.OnPageLoaded(view);
        }

        /// <summary>The page's process died (memory pressure): Helm stays up; the terminal comes back when the page is shown again.</summary>
        public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
        {
            host._ready = false;
            return true;
        }
    }

    /// <summary>
    /// Keys that are not text go straight to the session: with a keyboard's input connection open, Android's WebView
    /// reports Enter and Backspace to the page as "unidentified" (229), which xterm.js ignores. Text keeps the
    /// keyboard's own path (so composing, e.g. Vietnamese, works). A hardware Ctrl+letter sends its control code.
    /// Both halves of a handled key are taken, so the page never sees a lone key up.
    /// </summary>
    private bool HandleKey(global::Android.Views.Keycode keyCode, global::Android.Views.KeyEvent e)
    {
        if (!_ready) return false;
        string? send = keyCode switch
        {
            global::Android.Views.Keycode.Enter or global::Android.Views.Keycode.NumpadEnter => "\r",
            global::Android.Views.Keycode.Del => "\u007f",
            global::Android.Views.Keycode.ForwardDel => "\u001b[3~",
            global::Android.Views.Keycode.Tab => "\t",
            global::Android.Views.Keycode.Escape => "\u001b",
            _ => null,
        };
        string? key = keyCode switch
        {
            global::Android.Views.Keycode.DpadUp => "up",
            global::Android.Views.Keycode.DpadDown => "down",
            global::Android.Views.Keycode.DpadLeft => "left",
            global::Android.Views.Keycode.DpadRight => "right",
            global::Android.Views.Keycode.MoveHome => "home",
            global::Android.Views.Keycode.MoveEnd => "end",
            _ => null,
        };
        if (send is null && key is null && e.IsCtrlPressed)
        {
            var letter = (char)e.GetUnicodeChar(global::Android.Views.MetaKeyStates.None);
            send = letter == '\0' ? null : TerminalKeys.Ctrl(letter);
        }
        if (send is null && key is null) return false;
        if (e.Action == global::Android.Views.KeyEventActions.Down)
        {
            if (e.RepeatCount == 0) _pressed.Add(keyCode);
        }
        // The release of a key already sent is only taken; a release whose press never came (the keyboard app kept
        // it) is sent once.
        else if (_pressed.Remove(keyCode)) return true;
        if (key is not null) SendKey(key);
        else _viewModel.Send(send!);
        return true;
    }

    private sealed class Callback(TerminalHost host) : WebMessagePort.WebMessageCallback
    {
        public override void OnMessage(WebMessagePort? port, WebMessage? message) => host.OnMessage(message?.Data);
    }
}
