using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.Ssh;
using Helm.Modules.Ssh.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared SSH page (Helm.Modules.Ssh.UI) with its real view model over the Gallery's fake servers (nothing connects).</summary>
public sealed class SshViewTests : IDisposable
{
    private readonly FakeModule _module = new("ssh", "SSH");

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private static async Task<(Window Window, SshView View, SshViewModel Vm)> Show(SshStage stage, double width = 1200)
    {
        var view = (SshView)SshDemo.Create(new FakeModule("ssh", "SSH"), stage);
        var window = new Window { Content = view, Width = width, Height = 860 };
        window.Show();
        await Settle();
        return (window, view, (SshViewModel)view.DataContext!);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private static List<T> Shown<T>(Window window) where T : Control =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<T>().Where(c => c.IsEffectivelyVisible).Distinct().ToList();

    private static bool Says(Window window, string text) => Shown<TextBlock>(window).Any(t => t.Text == text);

    private static Control Named(Control root, string name) => root.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task A_card_per_server_the_one_shown_outlined_and_tapping_one_shows_it()
    {
        var (_, view, vm) = await Show(SshStage.Connected);
        var cards = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("server")).ToList();
        Assert.Equal(3, cards.Count);
        Assert.Single(cards, c => c.Classes.Contains("selected"));
        Assert.Same(vm.SelectedHost, cards.Single(c => c.Classes.Contains("selected")).DataContext);

        cards[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Assert.Equal("Raspberry Pi", vm.SelectedHost?.Title);
        Assert.Contains("selected", cards[2].Classes);
        // A password server not connected: the box to type it, no terminal.
        Assert.True(vm.NeedsPassword);
        Assert.True(Named(view, "PasswordBox").IsEffectivelyVisible);
        Assert.False(Named(view, "TerminalFrame").IsVisible);
    }

    [AvaloniaFact]
    public async Task The_terminal_steps_aside_while_a_sheet_covers_the_page()
    {
        var (_, view, _) = await Show(SshStage.Connected);
        var terminal = Named(view, "TerminalFrame");
        Assert.True(terminal.IsVisible);

        view.OpenSettings();
        await Settle();
        Assert.False(terminal.IsVisible);
        view.SettingsSheet!.Close();
        await Settle();
        Assert.True(terminal.IsVisible);

        await view.OpenMenuAsync();
        await Settle();
        Assert.False(terminal.IsVisible);
        view.MenuSheet!.Close();
        await Settle();
        Assert.True(terminal.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_server_menu_lists_its_groups_and_an_item_with_parameters_opens_its_form()
    {
        var (window, view, vm) = await Show(SshStage.Connected);
        await view.OpenMenuAsync();
        await Settle();
        Assert.True(vm.Menu.IsReady);
        Assert.True(Says(window, "DEPLOY"));
        Assert.True(Says(window, "Back up now"));

        await vm.Menu.ChooseAsync(vm.Menu.Entries.First(e => e.HasParams));
        await Settle();
        Assert.True(vm.Menu.IsFormOpen);
        Assert.True(Says(window, "SERVICE"));
        // Closing the sheet goes back to the list for next time.
        view.MenuSheet!.Close();
        await Settle();
        Assert.False(vm.Menu.IsFormOpen);
    }

    [AvaloniaFact]
    public async Task The_AI_agent_tab_shows_once_an_agent_ran_something_with_Stop_while_it_runs()
    {
        var (window, view, vm) = await Show(SshStage.Agent);
        Assert.True(vm.HasAgentTab);
        Assert.True(Named(view, "TabsRow").IsEffectivelyVisible);
        Assert.True(Says(window, "AI agent · running"));
        Assert.True(Says(window, "Stop"));

        ((RadioButton)Named(view, "ShellTab")).IsChecked = true;
        await Settle();
        Assert.False(vm.ShowingAgent);
        Assert.False(Says(window, "Stop"));
    }

    [AvaloniaFact]
    public async Task A_phone_gets_the_key_bar_under_the_terminal()
    {
        var (_, view, _) = await Show(SshStage.Phone, width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.True(Named(view, "KeyBar").IsVisible);
        var (_, wide, _) = await Show(SshStage.Connected);
        Assert.False(Named(wide, "KeyBar").IsVisible);
    }

    [AvaloniaFact]
    public async Task First_run_and_Add_open_the_settings_with_a_new_server_form()
    {
        var (window, view, vm) = await Show(SshStage.FirstRun);
        Assert.True(Says(window, "START HERE"));
        var add = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddFirstButton");
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Assert.True(vm.IsEditing);
        Assert.NotNull(view.SettingsSheet);
        Assert.True(Says(window, "Add a server"));
        Assert.True(Says(window, "SSH is on"));
        // Closing the sheet drops the form.
        view.SettingsSheet!.Close();
        await Settle();
        Assert.False(vm.IsEditing);
    }

    [AvaloniaFact]
    public async Task The_settings_keep_every_server_option_and_turning_SSH_off_shows_why_nothing_connects()
    {
        var (window, view, _) = await Show(SshStage.EditServer);
        Assert.True(Says(window, "AI agents may run shell commands"));
        Assert.True(Says(window, "Run AI agents' commands without asking"));
        Assert.True(Says(window, "Trusted server keys"));
        view.SettingsSheet!.Close();
        await Settle();

        var module = new FakeModule("ssh", "SSH");
        var page = (SshView)SshDemo.Create(module, SshStage.Connected);
        var other = new Window { Content = page, Width = 1200, Height = 860 };
        other.Show();
        module.IsEnabled = false;
        await Settle();
        Assert.True(Says(other, "SSH is turned off"));
        Assert.False(Named(page, "Picker").IsEnabled);
    }
}
