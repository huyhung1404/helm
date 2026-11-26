using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>
/// The open chapter's text (see the XAML). It scrolls to a paragraph when a chapter opens or the player seeks, follows the
/// sentence read aloud smoothly (keeping it between a tenth and two thirds of the way down) until the reader scrolls by
/// hand, tells the view model which paragraph is at the top (where reading stopped, synced), and puts that paragraph
/// back at the top when the text wraps again (a new width, text size or view). Ctrl + the wheel and a pinch change the
/// text size.
/// </summary>
public partial class NovelTextView : UserControl
{
    public static readonly StyledProperty<bool> ShowsPlayHereProperty = AvaloniaProperty.Register<NovelTextView, bool>(nameof(ShowsPlayHere), true);

    /// <summary>Scrolling within this long after the finger left the screen is still the reader's (a fling).</summary>
    private static readonly TimeSpan FlingTime = TimeSpan.FromSeconds(2);

    private NovelReaderViewModel? _viewModel;
    private double? _pinchStart;
    private bool _touching;
    private DateTime _lastTouch = DateTime.MinValue;
    private DateTime _autoScrollUntil = DateTime.MinValue;
    private DispatcherTimer? _animation;
    private double _width;

    /// <summary>The paragraph at the top of the view, kept there when the text wraps again.</summary>
    private int? _top;

    public NovelTextView()
    {
        InitializeComponent();
        ParagraphList.AddHandler(SegmentText.WordTappedEvent, OnWordTapped);
        ParagraphList.AddHandler(SegmentText.WordDoubleTappedEvent, OnWordDoubleTapped);
        ParagraphList.AddHandler(StudyText.WordHeldEvent, OnWordHeld);
        ParagraphList.AddHandler(PointerPressedEvent, OnParagraphPressed, RoutingStrategies.Tunnel);
        // Two fingers apart or together: larger or smaller text, from the size the pinch started at.
        Scroller.GestureRecognizers.Add(new PinchGestureRecognizer());
        Scroller.AddHandler(PinchEvent, (_, e) =>
        {
            if (_viewModel is null) return;
            _pinchStart ??= _viewModel.FontSize;
            _viewModel.ZoomFrom(_pinchStart.Value, e.Scale);
            e.Handled = true;
        });
        Scroller.AddHandler(PinchEndedEvent, (_, _) => _pinchStart = null);
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        Scroller.AddHandler(PointerPressedEvent, (_, _) => _touching = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(PointerReleasedEvent, (_, _) => EndTouch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(PointerCaptureLostEvent, (_, _) => EndTouch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>▶ in the margin of each paragraph (a wide screen with a mouse).</summary>
    public bool ShowsPlayHere
    {
        get => GetValue(ShowsPlayHereProperty);
        set => SetValue(ShowsPlayHereProperty, value);
    }

    /// <summary>A word was held: its sentence is explained (the page opens the sheet when the view model shows it).</summary>
    public event EventHandler<int>? SentenceHeld;

    /// <summary>A paragraph was right-clicked: its menu.</summary>
    public event EventHandler<ParagraphViewModel>? ParagraphMenu;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unhook();
        _viewModel = DataContext as NovelReaderViewModel;
        if (VisualRoot is not null) Hook();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unhook();
        _animation?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Hook()
    {
        if (_viewModel is null) return;
        _viewModel.ScrollRequested -= OnScrollRequested;
        _viewModel.FollowRequested -= OnFollowRequested;
        _viewModel.ScrollRequested += OnScrollRequested;
        _viewModel.FollowRequested += OnFollowRequested;
    }

    private void Unhook()
    {
        if (_viewModel is null) return;
        _viewModel.ScrollRequested -= OnScrollRequested;
        _viewModel.FollowRequested -= OnFollowRequested;
    }

    /// <summary>Back on the page: the sentence being read, or where reading stopped.</summary>
    public void ShowPlace()
    {
        if (_viewModel is not { } vm) return;
        if (vm.IsSpeaking) OnFollowRequested(this, vm.PlayerParagraph);
        else if (vm.IsBookOpen) OnScrollRequested(this, vm.PlayerParagraph);
    }

    // ---- Words and paragraphs -----------------------------------------------------------------------------------------

    private void OnWordTapped(object? sender, WordTappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is ParagraphViewModel paragraph) _viewModel?.OpenWord(paragraph, e.Segment);
    }

    private void OnWordDoubleTapped(object? sender, WordTappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not ParagraphViewModel paragraph || _viewModel is null) return;
        _viewModel.Editor.CloseCommand.Execute(null);
        _viewModel.ReadFromSegment(paragraph, e.Index);
    }

    /// <summary>A long press on a word: its sentence is highlighted and explained in a sheet.</summary>
    private void OnWordHeld(object? sender, WordHeldEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not ParagraphViewModel paragraph || _viewModel is null) return;
        _viewModel.Editor.CloseCommand.Execute(null);
        _viewModel.HoldSentence(paragraph, e.Index);
        SentenceHeld?.Invoke(this, paragraph.Index);
    }

    private void OnParagraphPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if ((e.Source as Control)?.FindAncestorOfType<Border>(includeSelf: true) is not { } border) return;
        if ((e.Source as Control)?.DataContext is not ParagraphViewModel paragraph || border.DataContext != paragraph) return;
        e.Handled = true;
        ParagraphMenu?.Invoke(this, paragraph);
    }

    private void ReadFromHere_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ParagraphViewModel paragraph) _viewModel?.ReadFrom(paragraph);
    }

    // ---- Scrolling ------------------------------------------------------------------------------------------------------

    /// <summary>The wheel: the reader looks elsewhere, so the text stops following the voice. With Ctrl it changes the text size.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0) _viewModel.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) _viewModel.ZoomOutCommand.Execute(null);
            e.Handled = true;
            return;
        }
        _autoScrollUntil = DateTime.MinValue;
        _lastTouch = DateTime.UtcNow;
        _animation?.Stop();
        _viewModel.StopFollowing();
    }

    private void EndTouch()
    {
        _touching = false;
        _lastTouch = DateTime.UtcNow;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Narrower or wider (the side panel, the window): every paragraph wraps again; the one at the top stays there.
        if (Math.Abs(e.NewSize.Width - _width) < 1) return;
        _width = e.NewSize.Width;
        if (_top is { } top) Dispatcher.UIThread.Post(() => KeepTop(top), DispatcherPriority.Loaded);
    }

    /// <summary>The text got another size or view (study mode, the Chinese under each paragraph): the paragraph at the top stays.</summary>
    public void Rewrapped()
    {
        if (_top is { } top) Dispatcher.UIThread.Post(() => KeepTop(top), DispatcherPriority.Loaded);
    }

    private void KeepTop(int index)
    {
        if (TopOf(index, sentence: false) is { } y) Jump(y - 4);
    }

    /// <summary>The reader scrolled (not the page following the voice): report the place, stop following.</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (TopParagraph() is { } top) _top = top;
        var now = DateTime.UtcNow;
        if (now < _autoScrollUntil || (!_touching && now - _lastTouch > FlingTime)) return;
        _animation?.Stop();
        _viewModel?.StopFollowing();
        if (_top is { } index) _viewModel?.ReportVisibleParagraph(index);
    }

    /// <summary>The paragraph at the top of the view.</summary>
    private int? TopParagraph()
    {
        if (_viewModel is null) return null;
        var offset = Scroller.Offset.Y;
        for (var i = 0; i < _viewModel.Paragraphs.Count; i++)
        {
            if (ParagraphList.ContainerFromIndex(i) is not Control container) continue;
            if (container.Bounds.Bottom + ParagraphList.Padding.Top > offset + 1) return i;
        }
        return null;
    }

    /// <summary>Where a paragraph (or the sentence being read in it) starts, from the top of the text.</summary>
    internal double? TopOf(int index, bool sentence)
    {
        if (index < 0 || ParagraphList.ContainerFromIndex(index) is not Control container) return null;
        var top = container.Bounds.Top + ParagraphList.Padding.Top;
        if (!sentence || _viewModel is null) return top;
        // The text shown: the words, or in study mode the word blocks.
        (Control Text, double? Inner)? shown = _viewModel.IsStudyMode
            ? container.GetVisualDescendants().OfType<StudyText>().FirstOrDefault() is { } study ? (study, study.HighlightTop()) : null
            : container.GetVisualDescendants().OfType<SegmentText>().FirstOrDefault() is { } text ? (text, text.HighlightTop()) : null;
        if (shown is { Inner: { } inner } found && found.Text.TranslatePoint(new Point(0, inner), container) is { } point) top += point.Y;
        return top;
    }

    /// <summary>A chapter opened, the seek bar or a novel reopened: show the paragraph at the top at once.</summary>
    private void OnScrollRequested(object? sender, int index) => Dispatcher.UIThread.Post(() =>
    {
        _top = Math.Max(0, index);
        if (TopOf(index, sentence: false) is not { } top)
        {
            if (index <= 0) Jump(0);
            return;
        }
        Jump(top - 8);
    }, DispatcherPriority.Loaded);

    /// <summary>Reading aloud moved on: keep its sentence between a tenth and two thirds of the way down, scrolling smoothly.</summary>
    private void OnFollowRequested(object? sender, int index) => Dispatcher.UIThread.Post(() =>
    {
        if (TopOf(index, sentence: true) is not { } top) return;
        var view = Scroller.Viewport.Height;
        var offset = Scroller.Offset.Y;
        if (top >= offset + view * 0.1 && top <= offset + view * 0.66) return;
        ScrollTo(top - view * 0.25);
    }, DispatcherPriority.Loaded);

    /// <summary>The held sentence's sheet covers the bottom of a phone: its line goes up into view above it.</summary>
    public void ShowHeld(int paragraph) => Dispatcher.UIThread.Post(() =>
    {
        if (TopOf(paragraph, sentence: true) is not { } top) return;
        var offset = Scroller.Offset.Y;
        if (top >= offset + 8 && top <= offset + Scroller.Viewport.Height * 0.3) return;
        ScrollTo(top - 16);
    }, DispatcherPriority.Loaded);

    private void Jump(double target)
    {
        _animation?.Stop();
        target = Math.Clamp(target, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        _autoScrollUntil = DateTime.UtcNow.AddMilliseconds(300);
        Scroller.Offset = new Vector(0, target);
    }

    private void ScrollTo(double target)
    {
        _animation?.Stop();
        target = Math.Clamp(target, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        var start = Scroller.Offset.Y;
        if (Math.Abs(target - start) > Scroller.Viewport.Height * 3)
        {
            Jump(target);
            return;
        }
        var began = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(280);
        _autoScrollUntil = began + duration + TimeSpan.FromMilliseconds(200);
        _animation = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - began) / duration);
            var eased = 1 - Math.Pow(1 - t, 3);
            Scroller.Offset = new Vector(0, start + (target - start) * eased);
            if (t >= 1) _animation?.Stop();
        });
        _animation.Start();
    }
}
