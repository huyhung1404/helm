using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Helm.Modules.Ssh.Terminal;

/// <summary>
/// The terminal of the selected server's session: xterm.js in WebView2. The page's files are served from this
/// assembly's resources (never written to disk, nothing loaded from the network), the browser features a terminal
/// does not need are off, and only messages from the page itself are read. Output is batched onto the UI thread and
/// sent as base64; switching servers redraws the terminal from the session's buffer. UI thread only.
/// </summary>
public partial class TerminalView
{
    internal const string Host = "helm-ssh.local";
    private const string Origin = "https://" + Host + "/";
    private const string PageUrl = Origin + "terminal.html";

    private static readonly Dictionary<string, (string Resource, string ContentType)> Files = new(StringComparer.Ordinal)
    {
        ["terminal.html"] = ("Helm.Ssh.terminal.html", "text/html; charset=utf-8"),
        ["terminal.js"] = ("Helm.Ssh.terminal.js", "text/javascript; charset=utf-8"),
        ["xterm.js"] = ("Helm.Ssh.xterm.js", "text/javascript; charset=utf-8"),
        ["xterm.css"] = ("Helm.Ssh.xterm.css", "text/css; charset=utf-8"),
        ["addon-fit.js"] = ("Helm.Ssh.addon-fit.js", "text/javascript; charset=utf-8"),
    };

    private readonly ConcurrentQueue<(int Generation, byte[] Bytes)> _pending = new();
    private SshViewModel? _viewModel;
    private IDialogService? _dialogs;
    private IClipboardService? _clipboard;
    private ILogger? _logger;
    private string _dataFolder = "";
    private CoreWebView2Environment? _environment;
    private Task? _init;
    private bool _ready;
    private SshSession? _attached;
    private Action<byte[]>? _sink;
    private int _generation;
    private int _flushQueued;

    public TerminalView()
    {
        InitializeComponent();
        // The terminal's own background (terminal.html), so nothing flashes white while it loads.
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0C, 0x0C, 0x0C);
        Loaded += (_, _) => _init ??= InitializeAsync();
    }

    internal void Attach(SshViewModel viewModel, IDialogService dialogs, IClipboardService clipboard, ILogger logger, string dataFolder)
    {
        _viewModel = viewModel;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _logger = logger;
        _dataFolder = dataFolder;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.TerminalFocusRequested += (_, _) => FocusTerminal();
    }

    /// <summary>Puts the keyboard focus in the terminal.</summary>
    public void FocusTerminal()
    {
        if (!_ready) return;
        Web.Focus();
        Post(new { t = "focus" });
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SshViewModel.ActiveSession)) Rebind();
        else if (e.PropertyName == nameof(SshViewModel.FontSize) && _viewModel is { } vm) Post(new { t = "font", s = vm.FontSize });
    }

    private async Task InitializeAsync()
    {
        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder).ConfigureAwait(true);
            await Web.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
            var core = Web.CoreWebView2;
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
                _logger?.LogWarning("The terminal's WebView2 process failed: {Kind}", e.ProcessFailedKind);
                _ready = false;
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Navigate(PageUrl);
            };
            core.Navigate(PageUrl);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The terminal could not start WebView2");
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
        if (_viewModel is not { } vm || !e.Source.StartsWith(Origin, StringComparison.Ordinal)) return;
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
                    vm.ActiveSession?.Send(Encoding.Latin1.GetBytes(root.GetProperty("d").GetString() ?? ""));
                    break;
                case "size":
                    vm.ReportTerminalSize(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                    break;
                case "copy":
                    if (root.GetProperty("d").GetString() is { Length: > 0 } text) _clipboard?.SetText(text);
                    break;
                case "paste":
                    _ = PasteAsync(root.TryGetProperty("b", out var b) && b.ValueKind == JsonValueKind.True);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _logger?.LogDebug("Unexpected message from the terminal page: {Type}", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Pastes the clipboard's text. Text with a line break is confirmed first, because each break runs a command and
    /// text copied from a web page can hide lines. Not needed when the shell asked for bracketed paste
    /// (<paramref name="bracketed"/>): it then waits for Enter.
    /// </summary>
    private async Task PasteAsync(bool bracketed)
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }
        if (text.Length == 0 || _viewModel?.ActiveSession?.IsConnected != true) return;
        if (TerminalPaste.NeedsConfirmation(text, bracketed) && _dialogs is { } dialogs
            && !await dialogs.ConfirmAsync("Paste text with line breaks?", TerminalPaste.Warning(text), "Paste").ConfigureAwait(true)) return;
        Post(new { t = "paste", d = TerminalPaste.ForTerminal(text) });
        FocusTerminal();
    }

    /// <summary>Shows the selected server's session: redraws from its buffer and takes its output from now on.</summary>
    private void Rebind()
    {
        if (_attached is { } old && _sink is { } oldSink) old.Detach(oldSink);
        _attached = null;
        _sink = null;
        var generation = Interlocked.Increment(ref _generation);
        if (!_ready || _viewModel is not { } vm) return;
        var session = vm.ActiveSession;
        if (session is null)
        {
            Post(new { t = "reset", d = "" });
            return;
        }
        Action<byte[]> sink = bytes =>
        {
            _pending.Enqueue((generation, bytes));
            if (Interlocked.Exchange(ref _flushQueued, 1) == 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
        };
        var snapshot = session.Attach(sink);
        _attached = session;
        _sink = sink;
        Post(new { t = "reset", d = Convert.ToBase64String(snapshot) });
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
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger?.LogDebug("Posting to the terminal failed: {Type}", ex.GetType().Name);
        }
    }

    private void ShowProblem(string? text)
    {
        Problem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Web.Visibility = text is null ? Visibility.Visible : Visibility.Hidden;
        ProblemText.Text = text ?? "";
    }
}
