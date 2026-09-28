using System.Collections.Concurrent;
using Helm.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Interop;

/// <summary>Drawing surface handed to an <see cref="OverlayWindow"/> paint callback. Coordinates are client pixels.</summary>
public interface IOverlayCanvas
{
    int Width { get; }
    int Height { get; }

    void Fill(PixelRect rect, RgbColor color);

    void Frame(PixelRect rect, RgbColor color, int thickness);

    void Text(PixelRect rect, string text, RgbColor color, int pixelHeight, bool bold = true);
}

/// <summary>
/// A borderless, click-through, never-activating layered window drawn with GDI
/// (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST).
/// Pixels painted with <see cref="TransparentKey"/> are fully transparent; everything else uses <see cref="Alpha"/>.
/// Must be created and used on one thread that pumps messages (a <see cref="MessageLoopThread"/>).
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    public static readonly RgbColor TransparentKey = new(1, 0, 1);

    private const string ClassName = "Helm.OverlayWindow";
    private static readonly WNDPROC s_wndProc = StaticWndProc; // rooted
    private static readonly ConcurrentDictionary<nint, OverlayWindow> s_windows = new();
    private static readonly object s_classLock = new();
    private static bool s_classRegistered;

    private readonly Action<IOverlayCanvas> _paint;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private HWND _hwnd;
    private byte _alpha;
    private PixelRect _bounds;

    private OverlayWindow(Action<IOverlayCanvas> paint, byte alpha)
    {
        _paint = paint;
        _alpha = alpha;
    }

    public nint Handle => _hwnd;

    public PixelRect Bounds => _bounds;

    public bool IsVisible { get; private set; }

    public byte Alpha
    {
        get => _alpha;
        set
        {
            _alpha = value;
            ApplyLayering();
        }
    }

    public static OverlayWindow Create(Action<IOverlayCanvas> paint, byte alpha = 255, string title = "Helm overlay")
    {
        EnsureClassRegistered();
        var window = new OverlayWindow(paint, alpha);
        var ex = WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                 | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST;
        unsafe
        {
            window._hwnd = PInvoke.CreateWindowEx(ex, ClassName, title, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, default, default, default, null);
        }
        if (window._hwnd.IsNull) throw new System.ComponentModel.Win32Exception();
        s_windows[window._hwnd] = window;
        window.ApplyLayering();
        return window;
    }

    /// <summary>Moves the overlay to <paramref name="screenRect"/> and keeps it at the top of the topmost band.</summary>
    public void SetBounds(PixelRect screenRect)
    {
        VerifyThread();
        var resized = screenRect.Width != _bounds.Width || screenRect.Height != _bounds.Height;
        _bounds = screenRect;
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        if (IsVisible) flags |= SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, screenRect.Left, screenRect.Top, screenRect.Width, screenRect.Height, flags);
        if (resized) Invalidate();
    }

    /// <summary>Re-asserts the overlay above other topmost windows without moving it.</summary>
    public void BringToTop()
    {
        VerifyThread();
        PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public void Show()
    {
        VerifyThread();
        if (IsVisible) return;
        IsVisible = true;
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        BringToTop();
        Invalidate();
    }

    public void Hide()
    {
        VerifyThread();
        if (!IsVisible) return;
        IsVisible = false;
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }

    public void Invalidate()
    {
        unsafe { PInvoke.InvalidateRect(_hwnd, (RECT*)null, false); }
    }

    public void Dispose()
    {
        if (_hwnd.IsNull) return;
        s_windows.TryRemove(_hwnd, out _);
        PInvoke.DestroyWindow(_hwnd);
        _hwnd = default;
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("OverlayWindow must be used on the thread that created it.");
    }

    private void ApplyLayering()
    {
        if (_hwnd.IsNull) return;
        PInvoke.SetLayeredWindowAttributes(_hwnd, TransparentKey.ToColorRef(), _alpha,
            LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_COLORKEY | LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    private void Paint()
    {
        var hdc = PInvoke.BeginPaint(_hwnd, out var ps);
        try
        {
            PInvoke.GetClientRect(_hwnd, out var client);
            var width = client.right - client.left;
            var height = client.bottom - client.top;
            if (width <= 0 || height <= 0) return;

            var memDc = PInvoke.CreateCompatibleDC(hdc);
            var bitmap = PInvoke.CreateCompatibleBitmap(hdc, width, height);
            var old = PInvoke.SelectObject(memDc, bitmap);
            try
            {
                var canvas = new GdiCanvas(memDc, width, height);
                canvas.Fill(new PixelRect(0, 0, width, height), TransparentKey);
                _paint(canvas);
                PInvoke.BitBlt(hdc, 0, 0, width, height, memDc, 0, 0, ROP_CODE.SRCCOPY);
            }
            finally
            {
                PInvoke.SelectObject(memDc, old);
                PInvoke.DeleteObject(bitmap);
                PInvoke.DeleteDC(memDc);
            }
        }
        finally
        {
            PInvoke.EndPaint(_hwnd, ps);
        }
    }

    private static unsafe void EnsureClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_classRegistered) return;
            fixed (char* className = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = s_wndProc,
                    hInstance = PInvoke.GetModuleHandle((string?)null),
                    lpszClassName = className,
                };
                if (PInvoke.RegisterClassEx(wc) == 0) throw new System.ComponentModel.Win32Exception();
            }
            s_classRegistered = true;
        }
    }

    private static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PInvoke.WM_NCHITTEST:
                return new LRESULT(-1); // HTTRANSPARENT: never take the mouse
            case PInvoke.WM_MOUSEACTIVATE:
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);
            case PInvoke.WM_ERASEBKGND:
                return new LRESULT(1);
            case PInvoke.WM_PAINT when s_windows.TryGetValue(hwnd, out var self):
                self.Paint();
                return default;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private sealed class GdiCanvas(HDC hdc, int width, int height) : IOverlayCanvas
    {
        public int Width { get; } = width;
        public int Height { get; } = height;

        public void Fill(PixelRect rect, RgbColor color)
        {
            if (rect.IsEmpty) return;
            var brush = PInvoke.CreateSolidBrush(color.ToColorRef());
            try { PInvoke.FillRect(hdc, rect.ToRect(), brush); }
            finally { PInvoke.DeleteObject(brush); }
        }

        public void Frame(PixelRect r, RgbColor color, int thickness)
        {
            if (r.IsEmpty || thickness <= 0) return;
            thickness = Math.Min(thickness, Math.Min(r.Width, r.Height) / 2 + 1);
            Fill(new PixelRect(r.Left, r.Top, r.Right, r.Top + thickness), color);
            Fill(new PixelRect(r.Left, r.Bottom - thickness, r.Right, r.Bottom), color);
            Fill(new PixelRect(r.Left, r.Top + thickness, r.Left + thickness, r.Bottom - thickness), color);
            Fill(new PixelRect(r.Right - thickness, r.Top + thickness, r.Right, r.Bottom - thickness), color);
        }

        public void Text(PixelRect rect, string text, RgbColor color, int pixelHeight, bool bold = true)
        {
            if (rect.IsEmpty || string.IsNullOrEmpty(text)) return;
            var font = PInvoke.CreateFont(-pixelHeight, 0, 0, 0, bold ? 600 : 400, 0, 0, 0,
                FONT_CHARSET.DEFAULT_CHARSET, FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS, FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
                FONT_QUALITY.CLEARTYPE_QUALITY, 0, "Segoe UI");
            var old = PInvoke.SelectObject(hdc, font);
            try
            {
                PInvoke.SetBkMode(hdc, BACKGROUND_MODE.TRANSPARENT);
                PInvoke.SetTextColor(hdc, color.ToColorRef());
                var r = rect.ToRect();
                unsafe
                {
                    fixed (char* p = text)
                    {
                        PInvoke.DrawText(hdc, p, text.Length, &r,
                            DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_VCENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX);
                    }
                }
            }
            finally
            {
                PInvoke.SelectObject(hdc, old);
                PInvoke.DeleteObject(font);
            }
        }
    }
}
