using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.Vault.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Ui.Tests;

/// <summary>Auto-type's picker (Vault on Windows) with its real view model over the Gallery's pretend logins.</summary>
public sealed class AutoTypePickerTests
{
    private static (Window Window, AutoTypePickerView View, AutoTypePickerViewModel Vm) Show(AutoTypeStage stage = AutoTypeStage.Likely)
    {
        var vm = VaultDemo.AutoTypePicker(stage);
        var view = new AutoTypePickerView(vm);
        var window = new Window { Content = view, Width = 492, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.FocusSearch();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void The_likely_logins_come_first_and_the_first_is_chosen()
    {
        var (window, view, vm) = Show();
        Assert.Equal(["Unity ID", "Unity Hub (work)", "Cloudflare", "GitHub"], vm.Rows.Take(4).Select(r => r.Title));
        Assert.Equal(8, vm.Rows.Count);
        Assert.Same(vm.Rows[0], vm.Selected);
        Assert.True(vm.Rows[0].IsSelected);
        Assert.True(vm.HasRemember);
        Assert.True(vm.Remember);
        Assert.True(Named<TextBox>(view, "SearchBox").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void Arrows_move_the_choice_and_Enter_types_it_with_remember()
    {
        var (window, _, vm) = Show();
        AutoTypeChoice? chosen = null;
        vm.Chosen += (_, c) => chosen = c;

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal("Unity Hub (work)", vm.Selected?.Title);
        Assert.False(vm.Rows[0].IsSelected);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.NotNull(chosen);
        Assert.Equal("Unity Hub (work)", chosen!.Entry.Item.Title);
        Assert.True(chosen.Remember);
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_searches_every_login_and_a_click_types_the_one_clicked()
    {
        var (window, view, vm) = Show();
        window.KeyTextInput("git");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["GitHub", "GitLab (studio)"], vm.Rows.Select(r => r.Title));
        Assert.Equal("GitHub", vm.Selected?.Title);

        AutoTypeChoice? chosen = null;
        vm.Chosen += (_, c) => chosen = c;
        var rows = Named<ItemsControl>(view, "List").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(2, rows.Count);
        rows[1].Command!.Execute(rows[1].CommandParameter);
        Assert.Equal("GitLab (studio)", chosen?.Entry.Item.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void No_match_says_so_and_Enter_types_nothing()
    {
        var (window, view, vm) = Show(AutoTypeStage.NoMatch);
        Assert.True(vm.IsEmpty);
        Assert.Null(vm.Selected);
        Assert.False(vm.HasRemember);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "No login matches. Try another word." && t.IsEffectivelyVisible);
        var chosen = false;
        vm.Chosen += (_, _) => chosen = true;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.False(chosen);
        window.Close();
    }

    [AvaloniaFact]
    public void Loading_again_clears_the_search_and_a_site_has_no_remember_line()
    {
        var vm = VaultDemo.AutoTypePicker(AutoTypeStage.Search);
        Assert.Equal(2, vm.Rows.Count);
        var logins = VaultDemo.AutoTypeLogins();
        vm.Load([logins[3]], logins, rememberText: null);
        Assert.Equal("", vm.Search);
        Assert.Equal("GitHub", vm.Rows[0].Title);
        Assert.False(vm.HasRemember);
        Assert.False(vm.Remember);
    }
}
