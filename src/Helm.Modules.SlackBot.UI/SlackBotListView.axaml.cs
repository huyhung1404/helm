using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Helm.Modules.SlackBot.Ui;

/// <summary>What only the window around Slack Report's list can do (Windows: SlackBotOverlayWindow).</summary>
public interface ISlackBotListHost
{
    /// <summary>Folded or opened: kept for the next time.</summary>
    void Folded(bool folded);

    /// <summary>A reply is written: the list takes the keyboard from the app in front.</summary>
    void TakeKeyboard();

    /// <summary>The reply is sent or cancelled: the keyboard goes back to the app that had it.</summary>
    void ReturnKeyboard();

    /// <summary>The list was pressed (not a button or the box): it moves with the mouse. True when it moved, false for a click.</summary>
    bool Drag(PointerPressedEventArgs e);

    /// <summary>A double-click: back to the corner.</summary>
    void BackToCorner();
}

/// <summary>
/// Slack Report's list on top of every app (see the XAML); DataContext: <see cref="SlackBotInboxViewModel"/>. See-through
/// while the pointer is elsewhere (the settings' opacity), solid under it, while a reply is written and for a few seconds
/// after a reminder.
/// </summary>
public partial class SlackBotListView : UserControl
{
    /// <summary>How long a reminder keeps it solid.</summary>
    public static readonly TimeSpan FlashTime = TimeSpan.FromSeconds(8);

    private readonly SlackBotInboxViewModel _inbox;
    private readonly ISlackBotListHost _host;
    private double _rest = 1;
    private bool _typing, _flashing;
    private IDisposable? _flash;

    public SlackBotListView() : this(null!, new NoHost(), null)
    {
    }

    /// <param name="logo">Slack Report's icon, beside its name.</param>
    public SlackBotListView(SlackBotInboxViewModel inbox, ISlackBotListHost host, IImage? logo, bool folded = false)
    {
        _inbox = inbox;
        _host = host;
        DataContext = inbox;
        InitializeComponent();
        FoldedLogo.Source = logo;
        OpenLogo.Source = logo;
        FoldButton.Click += (_, _) =>
        {
            SetFolded(true);
            _host.Folded(true);
        };
        ReplyBox.AddHandler(KeyDownEvent, OnReplyKey, RoutingStrategies.Tunnel);
        ReplyBox.AddHandler(PointerPressedEvent, (_, _) => StartTyping(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Folded.PointerPressed += OnPressed;
        Open.PointerPressed += OnPressed;
        Root.PointerEntered += (_, _) => UpdateOpacity();
        Root.PointerExited += (_, _) => UpdateOpacity();
        if (inbox is not null)
        {
            inbox.PropertyChanged += OnInboxChanged;
            DetachedFromVisualTree += (_, _) => inbox.PropertyChanged -= OnInboxChanged;
        }
        SetFolded(folded);
        UpdateOpacity();
    }

    public bool IsFolded => Folded.IsVisible;

    /// <summary>A reply is being written here (the list has the keyboard).</summary>
    public bool IsTyping => _typing;

    /// <summary>How solid it is now.</summary>
    public double CurrentOpacity => Root.Opacity;

    /// <summary>How solid it is while the pointer is elsewhere (0.3-1).</summary>
    public double RestOpacity
    {
        get => _rest;
        set
        {
            _rest = Math.Clamp(value, 0.3, 1);
            UpdateOpacity();
        }
    }

    public void SetFolded(bool fold)
    {
        Folded.IsVisible = fold;
        Open.IsVisible = !fold;
    }

    /// <summary>Solid for a few seconds, to be seen (a reminder).</summary>
    public void Flash()
    {
        _flashing = true;
        UpdateOpacity();
        _flash?.Dispose();
        _flash = DispatcherTimer.RunOnce(() =>
        {
            _flashing = false;
            UpdateOpacity();
        }, FlashTime);
    }

    /// <summary>Done writing (sent, cancelled, the window closed): the keyboard goes back.</summary>
    public void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        _host.ReturnKeyboard();
        UpdateOpacity();
    }

    private void UpdateOpacity() => Root.Opacity = Root.IsPointerOver || _typing || _flashing ? 1 : _rest;

    // Reply on a message: the box opens and takes the keyboard; closed (sent or cancelled): the keyboard goes back.
    private void OnInboxChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SlackBotInboxViewModel.ReplyTo)) return;
        if (_inbox.ReplyTo is null)
        {
            StopTyping();
            return;
        }
        if (IsFolded)
        {
            SetFolded(false);
            _host.Folded(false);
        }
        StartTyping();
    }

    private void StartTyping()
    {
        if (!_typing)
        {
            _typing = true;
            _host.TakeKeyboard();
            UpdateOpacity();
        }
        Dispatcher.UIThread.Post(() =>
        {
            ReplyBox.Focus();
            ReplyBox.CaretIndex = ReplyBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void OnReplyKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _inbox.CancelReplyCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            _inbox.SendReplyCommand.Execute(null);
        }
    }

    /// <summary>Dragging moves it; a click on the badge opens the list; a double-click puts it back in the corner.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _host.BackToCorner();
            return;
        }
        if (_host.Drag(e) || !IsFolded) return;
        // A click, not a drag: on the badge it opens the list.
        SetFolded(false);
        _host.Folded(false);
    }

    private sealed class NoHost : ISlackBotListHost
    {
        public void Folded(bool folded) { }
        public void TakeKeyboard() { }
        public void ReturnKeyboard() { }
        public bool Drag(PointerPressedEventArgs e) => false;
        public void BackToCorner() { }
    }
}
