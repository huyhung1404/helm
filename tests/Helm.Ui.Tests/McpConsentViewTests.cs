using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Mcp;
using Helm.Gallery;
using Helm.Mcp.Ui;

namespace Helm.Ui.Tests;

/// <summary>The question before an AI agent's tool call runs (McpConsentView, shown by McpConsentWindow on the PC).</summary>
public sealed class McpConsentViewTests
{
    private static (Window Window, McpConsentView View, List<McpConsentAnswer> Answers) Show(McpConsentStage stage)
    {
        var (screen, view) = McpConsentDemo.Build(stage);
        var answers = new List<McpConsentAnswer>();
        view.Answered += (_, a) => answers.Add(a);
        var window = new Window { Content = screen, Width = 900, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, answers);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static bool Shows(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    [AvaloniaFact]
    public void A_root_command_shows_who_what_where_and_exactly_what_runs()
    {
        var (window, view, _) = Show(McpConsentStage.RootCommand);
        Assert.True(Shows(view, "FAIRY 1.4.2 ASKS"));
        Assert.True(Shows(view, "Run a shell command on server “fairy-box”"));
        Assert.True(Shows(view, "fairy-box (root)"));
        Assert.True(Shows(view, "Exactly what will run"));
        Assert.Contains("full rights", Named<TextBlock>(view, "WarningText").Text);
        Assert.Equal("sudo systemctl restart nginx && journalctl -u nginx -n 20 --no-pager", Named<TextBox>(view, "DetailsText").Text);
        var session = Named<Button>(view, "AllowSessionButton");
        Assert.True(session.IsVisible);
        Assert.Equal("Allow this exact command for this session", session.Content);
        window.Close();
    }

    [AvaloniaFact]
    public void Allow_wakes_up_late_and_Deny_is_the_default()
    {
        var (window, view, answers) = Show(McpConsentStage.Change);
        // Asleep at first: a click meant for another window allows nothing.
        Assert.False(view.IsArmed);
        Assert.False(Named<Button>(view, "AllowOnceButton").IsEnabled);
        Assert.False(Named<Button>(view, "AllowSessionButton").IsEnabled);
        Assert.True(Named<Button>(view, "DenyButton").IsFocused);
        Assert.False(Shows(view, "Exactly what will run"));

        // Enter denies (once: a second key does nothing more).
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal([McpConsentAnswer.Deny], answers);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_denies_and_the_Allow_buttons_answer_once_awake()
    {
        var (window, view, answers) = Show(McpConsentStage.Change);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal([McpConsentAnswer.Deny], answers);
        window.Close();

        (window, view, answers) = Show(McpConsentStage.Change);
        view.Arm();
        Named<Button>(view, "AllowSessionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([McpConsentAnswer.AllowForSession], answers);
        window.Close();

        (window, view, answers) = Show(McpConsentStage.Dangerous);
        // Never for the session when dangerous.
        Assert.False(Named<Button>(view, "AllowSessionButton").IsVisible);
        view.Arm();
        Named<Button>(view, "AllowOnceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([McpConsentAnswer.AllowOnce], answers);
        window.Close();
    }

    [AvaloniaFact]
    public void The_Allow_buttons_wake_up_after_the_delay()
    {
        var (window, view, _) = Show(McpConsentStage.Change);
        Assert.False(view.IsArmed);
        var until = DateTime.UtcNow + McpConsentView.ArmDelay + TimeSpan.FromSeconds(2);
        while (!view.IsArmed && DateTime.UtcNow < until)
        {
            Thread.Sleep(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(view.IsArmed);
        window.Close();
    }
}
