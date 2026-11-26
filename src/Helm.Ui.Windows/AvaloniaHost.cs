using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Threading;
using Avalonia.Win32.Interoperability;
using Microsoft.Extensions.Logging;

namespace Helm.Ui.Windows;

/// <summary>
/// A WPF element showing one shared Avalonia page: <c>host.Show(() => new PageFrame("Tracker", new TrackerView(...)))</c>.
/// If Avalonia cannot start or the page cannot be built, it shows a short WPF message instead of failing the window.
/// </summary>
/// <remarks>
/// The page lives in its own child window (airspace): it ignores WPF render transforms, cannot be transparent (no Mica
/// behind it) and in-window WPF content cannot overlap it; WPF popups are separate windows and draw above it. A page
/// inside a WPF-UI Frame must opt out of the Frame's scroller (ScrollViewer.CanContentScroll="False" on the WPF page)
/// and scroll itself (PageFrame does).
/// </remarks>
public sealed class AvaloniaHost : ContentControl
{
    private Surface? _surface;

    /// <summary>Starts Avalonia before the first page; a test swaps it so no real Avalonia starts in the test process.</summary>
    internal static Action Starter { get; set; } = HelmAvalonia.EnsureStarted;

    public AvaloniaHost()
    {
        Focusable = false;
        IsTabStop = false;
    }

    /// <summary>The page now shown (null before Show, or when it failed).</summary>
    public Avalonia.Controls.Control? Page => _surface?.Inner.Content;

    /// <summary>Builds and shows the page; a failure shows a message in its place and is logged.</summary>
    public void Show(Func<Avalonia.Controls.Control> createPage)
    {
        try
        {
            Starter();
            var page = createPage();
            _surface ??= new Surface();
            _surface.Inner.Content = page;
            Content = _surface;
        }
        catch (Exception ex)
        {
            HelmAvalonia.Logger.LogError(ex, "A shared page could not open");
            if (_surface is not null) _surface.Inner.Content = null;
            Content = Failed(ex);
        }
    }

    private static UIElement Failed(Exception ex)
    {
        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "This page could not open.", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"{ex.GetBaseException().Message}\nThe details are in Helm's log.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.8,
        });
        return panel;
    }

    /// <summary>The child window holding the Avalonia page.</summary>
    private sealed class Surface : WindowsFormsHost
    {
        public readonly WinFormsAvaloniaControlHost Inner = new() { Dock = System.Windows.Forms.DockStyle.Fill };

        public Surface()
        {
            Child = Inner;
            // Only while on screen: a page left open on another tab must not take keys.
            Loaded += (_, _) =>
            {
                InputManager.Current.PostProcessInput -= ForwardSwallowedKey;
                InputManager.Current.PostProcessInput += ForwardSwallowedKey;
                // Shown (again): the page comes into focus over its backdrop (the WPF tab's own slide does not reach it).
                if (Inner.Content is Avalonia.Controls.Control shown) Helm.Core.Ui.Motion.EnterPage(shown);
                // The child window does not follow WPF render transforms such as the tab's slide-in and stays where the
                // animation started: re-place it once the animation is over (a layout pass re-syncs it).
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    InvalidateMeasure();
                    UpdateLayout();
                };
                timer.Start();
            };
            Unloaded += (_, _) => InputManager.Current.PostProcessInput -= ForwardSwallowedKey;
            LayoutUpdated += (_, _) => SyncCanvas();
        }

        private System.Windows.Rect _canvas;

        /// <summary>
        /// Tells the page where it sits in the window, so its Backdrop paints its part of the window-wide background
        /// (the shell's WindowBackdrop paints the rest) and the two meet without a seam.
        /// </summary>
        private void SyncCanvas()
        {
            if (Inner.Content is not Avalonia.Controls.Control page || Window.GetWindow(this)?.Content is not FrameworkElement root
                || !root.IsAncestorOf(this))
                return;
            var at = TranslatePoint(new System.Windows.Point(0, 0), root);
            var canvas = new System.Windows.Rect(-at.X, -at.Y, root.ActualWidth, root.ActualHeight);
            if (canvas == _canvas) return;
            _canvas = canvas;
            Helm.Core.Ui.Backdrop.SetCanvas(page, new Avalonia.Rect(canvas.X, canvas.Y, canvas.Width, canvas.Height));
        }

        /// <summary>
        /// While focus is in the page WPF still routes every key up its own tree first, and a key something there marks
        /// handled never reaches the page: WPF-UI's NavigationViewContentPresenter (a tab's Frame) takes Backspace so
        /// the Frame does not go back. Such a key is sent straight to the focused Avalonia window (SendMessage skips
        /// WPF's message pre-processing, so it is not taken a second time).
        /// </summary>
        private void ForwardSwallowedKey(object? sender, ProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is not KeyEventArgs { Handled: true } key
                || key.RoutedEvent != Keyboard.KeyDownEvent
                || key.Key is Key.ImeProcessed or Key.System or Key.DeadCharProcessed)
                return;
            // WPF has no focused element of its own while a hosted window has focus: ask Win32.
            var focus = GetFocus();
            if (focus == IntPtr.Zero || Handle == IntPtr.Zero || !IsChild(Handle, focus)) return;
            var virtualKey = KeyInterop.VirtualKeyFromKey(key.Key);
            var scan = MapVirtualKeyW((uint)virtualKey, 0);
            SendMessageW(focus, WM_KEYDOWN, virtualKey, (nint)(1 | (scan << 16)));
            SendMessageW(focus, WM_KEYUP, virtualKey, unchecked((nint)(1 | (scan << 16) | 0xC0000000)));
        }

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
        [DllImport("user32.dll")] private static extern nint SendMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);
    }
}
