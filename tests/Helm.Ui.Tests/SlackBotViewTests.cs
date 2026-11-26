using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.SlackBot;
using Helm.Modules.SlackBot.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Slack Report page (Helm.Modules.SlackBot.UI) with its real view models over the Gallery's inbox (no Slack).</summary>
public sealed class SlackBotViewTests : IDisposable
{
    private readonly FakeModule _module = new("slackbot", "Slack Report");

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private (Window Window, SlackBotView View, SlackBotInboxViewModel Inbox, SlackBotViewModel Settings) Show(SlackBotStage stage, double width = 1200)
    {
        var (view, inbox, settings) = SlackBotDemo.Build(_module, stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, inbox, settings);
    }

    /// <summary>What is on screen, the page and the sheets over it.</summary>
    private static List<TextBlock> Shown(Window window, string text) =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).Distinct().ToList();

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Visual Sheets(Window window) => OverlayLayer.GetOverlayLayer(window)!;

    [AvaloniaFact]
    public void Each_tab_replaces_the_lists_under_the_tabs()
    {
        var (window, view, inbox, _) = Show(SlackBotStage.ToDo);
        Assert.Equal(3, inbox.ToDo.Count);
        Assert.True(Named<StackPanel>(view, "ToDoPanel").IsVisible);
        Assert.False(Named<StackPanel>(view, "KnowingPanel").IsVisible);
        Assert.Contains("3 TO DO · 5 WORTH KNOWING", Named<TextBlock>(view, "TodayLine").Text);

        Named<RadioButton>(view, "KnowingTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<StackPanel>(view, "ToDoPanel").IsVisible);
        Assert.True(Named<StackPanel>(view, "KnowingPanel").IsVisible);
        Assert.Single(Shown(window, "Nothing to do about these. They show on screen together at the report times."));

        Named<RadioButton>(view, "ToDoTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<StackPanel>(view, "ToDoPanel").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Reply_opens_one_sheet_that_follows_the_view_model_and_closing_it_drops_the_reply()
    {
        var (window, view, inbox, _) = Show(SlackBotStage.ToDo);

        inbox.StartReplyCommand.Execute(inbox.ToDo[2]);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.ReplySheet);
        Assert.Single(Shown(window, "Reply to Trang (PO)"));

        // Another message while it is open: the same sheet, about the other message.
        var sheet = view.ReplySheet;
        inbox.StartReplyCommand.Execute(inbox.ToDo[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(sheet, view.ReplySheet);
        Assert.Single(Shown(window, "Reply to Tuấn (QA)"));

        // Back (or a tap outside, Escape) closes it, and nothing is being written any more.
        Assert.True(Sheet.CloseTopmost());
        Dispatcher.UIThread.RunJobs();
        Assert.Null(inbox.ReplyTo);
        Assert.Null(view.ReplySheet);

        // Cancel in the view model closes the sheet too.
        inbox.StartReplyCommand.Execute(inbox.ToDo[1]);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.ReplySheet);
        inbox.CancelReplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.ReplySheet);
        window.Close();
    }

    [AvaloniaFact]
    public void Sending_without_Slack_says_so_and_keeps_the_sheet_open()
    {
        var (window, view, inbox, _) = Show(SlackBotStage.Reply);
        Assert.NotNull(view.ReplySheet);

        inbox.SendReplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Slack Report is not connected to Slack right now.", inbox.ReplyProblem);
        Assert.True(view.ReplySheet?.IsOpen);
        Assert.Single(Shown(window, "Slack Report is not connected to Slack right now."));
        window.Close();
    }

    [AvaloniaFact]
    public void Done_moves_a_card_under_handled_today()
    {
        var (window, view, inbox, _) = Show(SlackBotStage.ToDo);
        Assert.Equal(2, inbox.Handled.Count);
        Assert.Equal("Handled today · 2", Named<TextBlock>(view, "HandledTitle").Text);
        Assert.False(Named<ItemsControl>(view, "HandledList").IsVisible);

        // Tuấn's card holds two messages of the DM: both are done, and each shows under handled today.
        inbox.DoneCommand.Execute(inbox.ToDo[0]);
        WaitFor(() => inbox.ToDo.Count == 2);
        Assert.Equal(4, inbox.Handled.Count);
        Assert.Equal("Handled today · 4", Named<TextBlock>(view, "HandledTitle").Text);

        view.ShowHandled();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<ItemsControl>(view, "HandledList").IsVisible);
        Assert.NotEmpty(Shown(window, "Marked done"));
        window.Close();
    }

    [AvaloniaFact]
    public void Not_signed_in_shows_the_first_run_card_whose_button_opens_the_steps()
    {
        var (window, view, _, settings) = Show(SlackBotStage.FirstRun);
        Assert.False(settings.HasTokens);
        Assert.True(Named<Border>(view, "FirstRun").IsVisible);
        Assert.False(Named<Border>(view, "TabGroup").IsVisible);
        Assert.False(Named<StackPanel>(view, "ToDoPanel").IsVisible);

        Assert.NotNull(view.OpenSettings(openConnection: true));
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<ToggleButton>(Sheets(window), "AppFold").IsChecked);
        Assert.Single(Shown(window, "Make the app"));

        // A token that cannot be right is refused before anything is saved or sent.
        Assert.False(settings.ConnectAsync("not-a-token", "xapp-1").GetAwaiter().GetResult());
        Assert.StartsWith("The user token starts with xoxp-", settings.TokenProblem);
        Assert.False(settings.HasTokens);
        window.Close();
    }

    [AvaloniaFact]
    public void The_settings_sheet_turns_the_tool_off_and_Write_needs_Read()
    {
        var (window, view, _, settings) = Show(SlackBotStage.ToDo);
        Assert.NotNull(view.OpenSettings(openConnection: false));
        Dispatcher.UIThread.RunJobs();
        // Connected: the app's steps are folded.
        Assert.False(Named<ToggleButton>(Sheets(window), "AppFold").IsChecked);

        var random = settings.Channels.Single(c => c.Name == "#random");
        Assert.False(random.CanRead);
        random.CanWrite = true;
        Assert.True(random.CanRead);
        random.CanRead = false;
        Assert.False(random.CanWrite);

        Named<ToggleSwitch>(Sheets(window), "EnableSwitch").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(_module.IsEnabled);
        Assert.Single(Shown(window, "Slack Report is turned off"));
        Assert.False(Named<StackPanel>(view, "Body").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void The_model_list_saves_the_value_behind_its_label()
    {
        var (window, view, _, settings) = Show(SlackBotStage.ToDo);
        view.OpenSettings(openConnection: false);
        Dispatcher.UIThread.RunJobs();
        var models = Named<ComboBox>(Sheets(window), "ModelBox");
        Assert.Equal(SlackAiOptions.Models.ToList().FindIndex(m => m.Value == "sonnet"), models.SelectedIndex);

        models.SelectedIndex = SlackAiOptions.Models.ToList().FindIndex(m => m.Value == "haiku");
        Assert.Equal("haiku", settings.Model);
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_puts_the_tabs_under_today_and_the_actions_as_icons()
    {
        var (window, view, _, _) = Show(SlackBotStage.ToDo, width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(1, Grid.GetRow(Named<StackPanel>(view, "Tabs")));
        Assert.Empty(Shown(window, "Open in Slack"));
        window.Close();
    }

    private static void WaitFor(Func<bool> done)
    {
        // The view model follows the inbox a moment after it changes (a timer, then the UI thread).
        for (var i = 0; i < 100 && !done(); i++)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }
}
