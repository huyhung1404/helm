using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// Read-only, selectable markdown. While <see cref="Markdown"/> keeps changing (a reply streaming in) the document is
/// rebuilt at most every <see cref="RenderInterval"/>, so a long answer never re-lays out per token.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(80);

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(string.Empty, (d, _) => ((MarkdownView)d).ScheduleRender()));

    private readonly DispatcherTimer _timer;
    private DateTime _lastRender = DateTime.MinValue;
    private string? _rendered;

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true; // hyperlinks respond to clicks in a read-only box
        IsUndoEnabled = false;
        BorderThickness = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent;
        Padding = new Thickness(0);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = true;
        Cursor = Cursors.IBeam;
        Template = CreateTemplate();
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = RenderInterval };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Render();
        };
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnRequestNavigate));
        Document = MarkdownRenderer.Render(string.Empty);
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void ScheduleRender()
    {
        if (DateTime.UtcNow - _lastRender >= RenderInterval)
        {
            _timer.Stop();
            Render();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start(); // the latest text is rendered when the interval ends
        }
    }

    private void Render()
    {
        var markdown = Markdown ?? string.Empty;
        _lastRender = DateTime.UtcNow;
        if (markdown == _rendered) return;
        _rendered = markdown;

        // Move the new blocks into the existing document rather than replacing it: the RichTextBox's automation
        // peer keeps reading the document it started with, so screen readers would hear stale text.
        var fresh = MarkdownRenderer.Render(markdown);
        var blocks = fresh.Blocks.ToList();
        fresh.Blocks.Clear();
        Document.Blocks.Clear();
        Document.Blocks.AddRange(blocks);
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!MarkdownRenderer.IsSafeLink(e.Uri?.ToString(), out var uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No handler for the scheme; nothing to do.
        }
    }

    /// <summary>Just the content host: none of the themed input-box chrome.</summary>
    private static ControlTemplate CreateTemplate()
    {
        var template = new ControlTemplate(typeof(RichTextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        template.VisualTree = host;
        return template;
    }
}
