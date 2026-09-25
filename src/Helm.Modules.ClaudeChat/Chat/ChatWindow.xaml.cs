using System.ComponentModel;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// The floating home of <see cref="ClaudeChatView"/> once the user drags it out of the page. Closing it (✕ or
/// "Back to Helm") puts the chat back on the page instead — the conversation keeps running either way.
/// </summary>
public partial class ChatWindow : FluentWindow
{
    private bool _closeForGood;

    public ChatWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplicationThemeManager.Apply(this);
    }

    /// <summary>The user asked to put the chat back on the page (the window hides afterwards).</summary>
    public event EventHandler? DockRequested;

    public ClaudeChatView? View
    {
        get => Host.Content as ClaudeChatView;
        set => Host.Content = value;
    }

    /// <summary>Distance from the window's top edge to the chat view, in DIPs (title bar).</summary>
    public double ViewTop => TitleBar.ActualHeight > 0 ? TitleBar.ActualHeight : 32;

    /// <summary>Really closes the window (module turned off, Helm exiting).</summary>
    public void CloseForGood()
    {
        _closeForGood = true;
        Close();
    }

    private void OnDockClick(object sender, RoutedEventArgs e) => DockRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeForGood && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            DockRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }
}
