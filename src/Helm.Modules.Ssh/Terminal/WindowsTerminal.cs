using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Helm.Modules.Ssh.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Helm.Modules.Ssh.Terminal;

/// <summary>
/// The terminal of the selected server's session on Windows: xterm.js in WebView2, inside the shared Avalonia SSH page
/// through a <see cref="NativeControlHost"/> (Avalonia gives it a child window, WebView2 draws in that window). The page's
/// files are served from this assembly's resources (never written to disk, nothing loaded from the network), the browser
/// features a terminal does not need are off, and only messages from the page itself are read. Output is batched onto the UI
/// thread and sent as base64; switching servers (or the AI agent tab) redraws the terminal from the buffer. Being a native
/// window it is drawn over the page: the page hides it while a sheet is open (<see cref="SshView"/>). UI thread only.
/// </summary>
internal sealed class WindowsTerminal : NativeControlHost, ISshTerminal
{
    private const string Origin = "https://helm-ssh.local/";
    private const string PageUrl = Origin + "terminal.html";

    private static readonly Dictionary<string, (string Resource, string ContentType)> Files = new(StringComparer.Ordinal)
    {
        ["terminal.html"] = ("Helm.Ssh.terminal.html", "text/html; charset=utf-8"),
        ["terminal.js"] = ("Helm.Ssh.terminal.js", "text/javascript; charset=utf-8"),
        ["xterm.js"] = ("Helm.Ssh.xterm.js", "text/javascript; charset=utf-8"),
        ["xterm.css"] = ("Helm.Ssh.xterm.css", "text/css; charset=utf-8"),
        ["addon-fit.js"] = ("Helm.Ssh.addon-fit.js", "text/javascript; charset=utf-8"),
    };

    /// <summary>One WebView2 environment (one browser process group, one profile folder) for every terminal of this run.</summary>
    private static Task<CoreWebView2Environment>? s_environment;

    private readonly ConcurrentQueue<(int Generation, byte[] Bytes)> _pending = new();
    private readonly SshViewModel _viewModel;
    private readonly IClipboardService _clipboard;
    private readonly ILogger _logger;
    private readonly string _dataFolder;
    private readonly Border _problem;
    private readonly TextBlock _problemText;
    private IPlatformHandle? _window;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private bool _ready;
    private ITerminalSource? _attached;
    private Action<byte[]>? _sink;
    private int _generation;
    private int _flushQueued;
    private (int Width, int Height) _size;

    public WindowsTerminal(SshViewModel viewModel, IClipboardService clipboard, ILogger logger, string dataFolder)
    {
        _viewModel = viewModel;
        _clipboard = clipboard;
        _logger = logger;
        _dataFolder = dataFolder;
        _problemText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 480 };
        _problem = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)),
            Padding = new Avalonia.Thickness(24),
            IsVisible = false,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _problemText },
            },
        };
        _problemText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        View = new Grid { Children = { this, _problem } };
    }

    /// <summary>The terminal and, over it, the message shown when WebView2 cannot start.</summary>
    public Avalonia.Controls.Control View { get; }

    /// <summary>Puts the keyboard focus in the terminal.</summary>
    void ISshTerminal.Focus()
    {
        if (!_ready || _controller is not { } controller) return;
        try
        {
            controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _logger.LogDebug("Focusing the terminal failed: {Type}", ex.GetType().Name);
        }
        Post(new { t = "focus" });
    }

    /// <summary>A key from the key bar (esc, tab, up, ...); the page sends it as the terminal would.</summary>
    public void SendKey(string key) => Post(new { t = "key", k = key });

    Task ISshTerminal.PasteAsync() => PasteAsync(bracketed: false);

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // Avalonia's own child window is the parent of the web view: Avalonia places and sizes it, the web view fills it.
        var window = base.CreateNativeControlCore(parent);
        _window = window;
        _viewModel.PropertyChanged += OnViewModelChanged;
        LayoutUpdated += OnLayoutUpdated;
        _ = InitializeAsync(window);
        return window;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        LayoutUpdated -= OnLayoutUpdated;
        Detach();
        _ready = false;
        _window = null;
        var controller = _controller;
        _controller = null;
        try
        {
            controller?.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _logger.LogDebug("Closing the terminal's web view failed: {Type}", ex.GetType().Name);
        }
        base.DestroyNativeControlCore(control);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SshViewModel.ActiveTerminal)) Rebind();
        else if (e.PropertyName == nameof(SshViewModel.FontSize)) Post(new { t = "font", s = _viewModel.FontSize });
    }

    /// <summary>Avalonia moved or resized its child window: the web view takes the new size.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e) => Fit();

    private void Fit()
    {
        if (_controller is not { } controller || _window is not { } window || !GetClientRect(window.Handle, out var rect)) return;
        var size = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (size == _size || size.Item1 <= 0 || size.Item2 <= 0) return;
        _size = size;
        try
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, size.Item1, size.Item2);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _logger.LogDebug("Sizing the terminal failed: {Type}", ex.GetType().Name);
        }
    }

    private async Task InitializeAsync(IPlatformHandle window)
    {
        try
        {
            // WebView2 keeps its profile in Helm's cache (the install folder may not be writable). Nothing of the session
            // is stored there: the page's files come from resources and the terminal keeps no history on disk.
            s_environment ??= CoreWebView2Environment.CreateAsync(null, _dataFolder);
            _environment = await s_environment.ConfigureAwait(true);
            var controller = await _environment.CreateCoreWebView2ControllerAsync(window.Handle).ConfigureAwait(true);
            if (_window?.Handle != window.Handle)
            {
                // The page left the screen while WebView2 was starting.
                controller.Close();
                return;
            }
            _controller = controller;
            // The terminal's own background (terminal.html), so nothing flashes white while it loads.
            controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0C, 0x0C, 0x0C);
            _size = default;
            Fit();
            var core = controller.CoreWebView2;
            var settings = core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsSwipeNavigationEnabled = false;
            // Ctrl+F, Ctrl+P, F5 and the like belong to the shell inside the terminal, not to the browser.
            settings.AreBrowserAcceleratorKeysEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.NavigationStarting += (_, e) =>
            {
                if (!string.Equals(e.Uri, PageUrl, StringComparison.Ordinal)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += (_, e) =>
            {
                _logger.LogWarning("The terminal's WebView2 process failed: {Kind}", e.ProcessFailedKind);
                _ready = false;
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Navigate(PageUrl);
            };
            core.Navigate(PageUrl);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The environment is kept: a failed one (no runtime) fails the same way next time, and says so.
            _logger.LogWarning(ex, "The terminal could not start WebView2");
            ShowProblem($"The terminal could not start ({ex.Message}). It needs the Microsoft Edge WebView2 Runtime.");
        }
    }

    /// <summary>Serves the page's own files from resources; anything else (any other host or path) gets a 403.</summary>
    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_environment is null) return;
        var uri = e.Request.Uri;
        if (uri.StartsWith(Origin, StringComparison.Ordinal) && Files.TryGetValue(uri[Origin.Length..], out var file)
            && Assembly.GetExecutingAssembly().GetManifestResourceStream(file.Resource) is { } stream)
        {
            e.Response = _environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {file.ContentType}\r\nCache-Control: no-store");
            return;
        }
        e.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.Ordinal)) return;
        var vm = _viewModel;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "ready":
                    _ready = true;
                    ShowProblem(null);
                    Post(new { t = "font", s = vm.FontSize });
                    vm.ReportTerminalSize(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                    Rebind();
                    break;
                case "in":
                    vm.Send(root.GetProperty("d").GetString() ?? "");
                    break;
                case "bin":
                    // xterm.js hands binary input (some mouse reports) as a string of byte values.
                    vm.Send(Encoding.Latin1.GetBytes(root.GetProperty("d").GetString() ?? ""));
                    break;
                case "size":
                    vm.ReportTerminalSize(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
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
            _logger.LogDebug("Unexpected message from the terminal page: {Type}", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Pastes the clipboard's text. Text with a line break is confirmed first, because each break runs a command and text
    /// copied from a web page can hide lines. Not needed when the shell asked for bracketed paste (<paramref name="bracketed"/>):
    /// it then waits for Enter.
    /// </summary>
    private async Task PasteAsync(bool bracketed)
    {
        string text;
        try
        {
            text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        }
        catch (ExternalException)
        {
            return;
        }
        if (text.Length == 0 || _viewModel is not { CanType: true, ActiveSession.IsConnected: true }) return;
        if (TerminalPaste.NeedsConfirmation(text, bracketed)
            && !await _viewModel.AskAsync("Paste text with line breaks?", TerminalPaste.Warning(text), "Paste").ConfigureAwait(true)) return;
        Post(new { t = "paste", d = TerminalPaste.ForTerminal(text) });
        ((ISshTerminal)this).Focus();
    }

    /// <summary>
    /// Shows the selected server's shell, or what agents run there (its AI agent tab, read-only): redraws from its buffer and
    /// takes its output from now on.
    /// </summary>
    private void Rebind()
    {
        Detach();
        var generation = Volatile.Read(ref _generation);
        if (!_ready) return;
        var source = _viewModel.ActiveTerminal;
        Post(new { t = "ro", v = source is AgentConsole });
        if (source is null)
        {
            Post(new { t = "reset", d = "" });
            return;
        }
        Action<byte[]> sink = bytes =>
        {
            _pending.Enqueue((generation, bytes));
            if (Interlocked.Exchange(ref _flushQueued, 1) == 0) Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        };
        var snapshot = source.Attach(sink);
        _attached = source;
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
        if (!_ready) return;
        try
        {
            _controller?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _logger.LogDebug("Posting to the terminal failed: {Type}", ex.GetType().Name);
        }
    }

    /// <summary>A message in place of the terminal (the web view is hidden behind it), or the terminal again (null).</summary>
    private void ShowProblem(string? text)
    {
        _problem.IsVisible = text is not null;
        IsVisible = text is null;
        _problemText.Text = text ?? "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);
}
