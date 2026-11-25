using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.Scratch;
using Helm.Modules.Scratch.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Scratch page (Helm.Modules.Scratch.UI) with its real view model over an in-memory store.</summary>
public sealed class ScratchViewTests
{
    private static (Window Window, ScratchView View, ScratchViewModel Vm) Show(ScratchStage stage, double width = 1200, bool detail = false, FakeModule? module = null)
    {
        var view = (ScratchView)ScratchDemo.Create(module ?? new FakeModule("scratch", "Scratch"), stage, detail);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, (ScratchViewModel)view.DataContext!);
    }

    private static bool Shown(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    [AvaloniaFact]
    public void The_wall_shows_every_thing_and_counts_the_trash()
    {
        var (window, view, vm) = Show(ScratchStage.Wall);

        Assert.Equal(11, vm.Rows.Count);
        Assert.Equal("11 things", vm.ListSummary);
        Assert.Equal(2, vm.TrashCount);
        Assert.False(Shown(view, "DetailLayer"));
        window.Close();
    }

    [AvaloniaFact]
    public void On_a_wide_page_selecting_a_card_does_not_open_details()
    {
        var (window, view, vm) = Show(ScratchStage.Wall);

        vm.SelectCommand.Execute(vm.Rows[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.Rows[0].IsSelected);
        Assert.False(Shown(view, "DetailLayer"));
        Assert.True(Shown(view, "WallLayer"));
        window.Close();
    }

    [AvaloniaFact]
    public void On_a_phone_a_tap_shows_the_details_in_the_walls_place_and_closing_returns()
    {
        var (window, view, vm) = Show(ScratchStage.Wall, width: 390, detail: true);

        Assert.True(Shown(view, "DetailLayer"));
        Assert.False(Shown(view, "WallLayer"));

        vm.CloseDetail();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Shown(view, "WallLayer"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_trash_button_switches_to_the_trash_and_back()
    {
        var (window, view, vm) = Show(ScratchStage.Wall);

        vm.ToggleTrashCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ShowingTrash);
        Assert.Equal("Trash", vm.ListTitle);
        Assert.Equal(2, vm.Rows.Count);

        vm.ToggleTrashCommand.Execute(null);
        Assert.False(vm.ShowingTrash);
        Assert.Equal(11, vm.Rows.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Searching_narrows_the_wall_and_stops_reordering()
    {
        var (window, _, vm) = Show(ScratchStage.Wall);
        Assert.True(vm.CanReorder);

        vm.Search = "ssh";
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.Rows);
        Assert.False(vm.CanReorder);
        Assert.StartsWith("1 found", vm.ListSummary);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(11, vm.Rows.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Dragging_a_card_onto_another_moves_it()
    {
        var (window, _, vm) = Show(ScratchStage.Wall);
        var first = vm.Rows[0].Id;
        var third = vm.Rows[2].Id;

        vm.Move(first, third);
        // The wall rebuilds a moment after the store changes.
        for (var i = 0; i < 20 && vm.Rows[0].Id == first; i++)
        {
            Thread.Sleep(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotEqual(first, vm.Rows[0].Id);
        window.Close();
    }

    [AvaloniaFact]
    public void An_empty_page_says_how_to_add_something()
    {
        var (window, view, vm) = Show(ScratchStage.Empty);

        Assert.True(vm.IsListEmpty);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "AddHint" && t.Text!.Contains("Ctrl+V"));
        window.Close();
    }

    [AvaloniaFact]
    public void While_Scratch_is_off_it_says_so_and_cannot_add()
    {
        var (window, view, _) = Show(ScratchStage.Wall, module: new FakeModule("scratch", "Scratch") { IsEnabled = false });

        Assert.True(Shown(view, "OffBar"));
        Assert.False(view.FindControl<Button>("FilesButton")!.IsEnabled);
        window.Close();
    }
}
