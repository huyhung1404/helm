using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Mcp;

namespace Helm.Mcp.Ui;

/// <summary>
/// The question for one AI agent's tool call (see the XAML); DataContext: <see cref="McpConsentText"/>. <see cref="Answered"/>
/// says Deny unless an Allow button is pressed once they are awake (<see cref="ArmDelay"/> after the view is shown).
/// Enter and Escape deny, unless an awake Allow button has the keyboard focus (then Enter presses it).
/// </summary>
public partial class McpConsentView : UserControl
{
    /// <summary>How long the Allow buttons stay off after the question is shown.</summary>
    public static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>The eyebrow line in capitals.</summary>
    public static readonly IValueConverter Upper = new FuncValueConverter<string, string>(t => t?.ToUpperInvariant() ?? "");

    private readonly McpConsentText _text;
    private bool _answered;

    public McpConsentView() : this(null!)
    {
    }

    public McpConsentView(McpConsentText text)
    {
        _text = text;
        DataContext = text;
        InitializeComponent();
        DenyButton.Click += (_, _) => Answer(McpConsentAnswer.Deny);
        AllowOnceButton.Click += (_, _) => Answer(McpConsentAnswer.AllowOnce);
        AllowSessionButton.Click += (_, _) => Answer(_text.OfferSession ? McpConsentAnswer.AllowForSession : McpConsentAnswer.AllowOnce);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            DragRequested?.Invoke(this, e);
        };
        AttachedToVisualTree += (_, _) =>
        {
            // Deny has the keyboard, so Enter and Escape reach this view; the Allow buttons wake up a moment later.
            Dispatcher.UIThread.Post(() => DenyButton.Focus(), DispatcherPriority.Loaded);
            _ = Task.Delay(ArmDelay).ContinueWith(_ => Dispatcher.UIThread.Post(Arm), TaskScheduler.Default);
        };
    }

    /// <summary>The user answered (once): Deny, Allow once or Allow for this session.</summary>
    public event EventHandler<McpConsentAnswer>? Answered;

    /// <summary>The sheet (not a button or the details) was pressed: the window may move with the mouse.</summary>
    public event EventHandler<PointerPressedEventArgs>? DragRequested;

    /// <summary>The Allow buttons are awake.</summary>
    public bool IsArmed => AllowOnceButton.IsEnabled;

    /// <summary>Wakes the Allow buttons (after <see cref="ArmDelay"/>).</summary>
    public void Arm()
    {
        AllowOnceButton.IsEnabled = true;
        AllowSessionButton.IsEnabled = true;
    }

    /// <summary>Closing the window, or a key the view did not get: Deny (once).</summary>
    public void Deny() => Answer(McpConsentAnswer.Deny);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Answer(McpConsentAnswer.Deny);
        }
        else if (e.Key == Key.Enter)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            // Enter presses an awake Allow button the user moved to on purpose; anywhere else it denies.
            if (focused is Button { IsEnabled: true } button && (button == AllowOnceButton || button == AllowSessionButton)) return;
            e.Handled = true;
            Answer(McpConsentAnswer.Deny);
        }
    }

    private void Answer(McpConsentAnswer answer)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(this, answer);
    }
}
