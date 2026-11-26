using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.SlackBot;
using Helm.Modules.SlackBot.Ui;

namespace Helm.Ui.Tests;

/// <summary>Slack Report's list that stays on screen on the PC (SlackBotListView, shown by SlackBotOverlayWindow), over the Gallery's inbox (no Slack).</summary>
public sealed class SlackBotListViewTests
{
    private readonly FakeModule _module = new("slackbot", "Slack Report");

    private sealed class Host : ISlackBotListHost
    {
        public List<string> Calls { get; } = [];

        public void Folded(bool folded) => Calls.Add(folded ? "folded" : "opened");

        public void TakeKeyboard() => Calls.Add("take");

        public void ReturnKeyboard() => Calls.Add("return");

        public bool Drag(PointerPressedEventArgs e) => false;

        public void BackToCorner() => Calls.Add("corner");
    }

    private (Window Window, SlackBotListView List, SlackBotInboxViewModel Inbox, Host Host) Show(SlackListStage stage)
    {
        var host = new Host();
        var (screen, list, inbox) = SlackBotDemo.BuildList(_module, stage, host);
        var window = new Window { Content = screen, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, list, inbox, host);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void The_list_shows_what_waits_and_is_see_through_at_rest()
    {
        var (window, list, inbox, _) = Show(SlackListStage.Open);
        Assert.False(list.IsFolded);
        Assert.Equal(3, inbox.ToDo.Count);
        Assert.Equal(3, Named<ItemsControl>(list, "ToDoList").GetRealizedContainers().Count());
        Assert.False(Named<Border>(list, "ReplyPanel").IsVisible);
        Assert.Equal(0.85, list.CurrentOpacity, 3);

        // A reminder: solid for a while.
        list.Flash();
        Assert.Equal(1, list.CurrentOpacity);

        // Never below 0.3, whatever the setting.
        list.RestOpacity = 0.1;
        Assert.Equal(0.3, list.RestOpacity, 3);
        window.Close();
    }

    [AvaloniaFact]
    public void Fold_keeps_a_badge_and_a_reply_opens_it_and_takes_the_keyboard()
    {
        var (window, list, inbox, host) = Show(SlackListStage.Open);
        Named<Button>(list, "FoldButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsFolded);
        Assert.True(Named<Border>(list, "Folded").IsVisible);
        Assert.False(Named<Border>(list, "Open").IsVisible);
        Assert.Equal(["folded"], host.Calls);

        // Reply (from the page or the MCP's question): the list opens, takes the keyboard, the box has the cursor.
        inbox.StartReplyCommand.Execute(inbox.ToDo[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.False(list.IsFolded);
        Assert.True(list.IsTyping);
        Assert.Equal(1, list.CurrentOpacity);
        Assert.True(Named<Border>(list, "ReplyPanel").IsVisible);
        var box = Named<TextBox>(list, "ReplyBox");
        Assert.True(box.IsFocused);
        Assert.Equal(["folded", "opened", "take"], host.Calls);

        // Escape closes the reply: the keyboard goes back.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(inbox.ReplyTo);
        Assert.False(list.IsTyping);
        Assert.Equal(["folded", "opened", "take", "return"], host.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void The_folded_badge_shows_the_count()
    {
        var (window, list, inbox, _) = Show(SlackListStage.Folded);
        Assert.True(list.IsFolded);
        var badge = Named<Border>(list, "Folded");
        Assert.Contains(badge.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == inbox.ToDoCount.ToString() && t.IsEffectivelyVisible);
        window.Close();
    }
}
