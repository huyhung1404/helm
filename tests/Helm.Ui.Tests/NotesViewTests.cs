using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.Notes;
using Helm.Modules.Notes.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Notes page (Helm.Modules.Notes.UI) with its real view model over an in-memory store.</summary>
public sealed class NotesViewTests
{
    private static (Window Window, NotesView View, NotesViewModel Vm) Show(NotesStage stage, double width = 1200, FakeModule? module = null)
    {
        var view = (NotesView)NotesDemo.Create(module ?? new FakeModule("notes", "Notes"), stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, (NotesViewModel)view.DataContext!);
    }

    private static bool Shown(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static List<TextBlock> Texts(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).ToList();

    [AvaloniaFact]
    public void The_wall_shows_every_note_as_a_card_with_its_count()
    {
        var (window, view, vm) = Show(NotesStage.Wall);

        Assert.Equal("10 notes · 2 pinned", vm.ListSummary);
        Assert.Single(Texts(view, "Wifi nhà bà ngoại"));
        Assert.False(Shown(view, "EditorLayer"));
        window.Close();
    }

    [AvaloniaFact]
    public void On_a_wide_screen_the_open_note_floats_over_the_wall_and_Esc_closes_it()
    {
        var (window, view, vm) = Show(NotesStage.Open);

        Assert.True(vm.IsEditorOpen);
        Assert.True(Shown(view, "EditorLayer"));
        Assert.True(Shown(view, "WallLayer"));
        Assert.True(Shown(view, "Scrim"));

        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsEditorOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void On_a_phone_the_open_note_takes_the_walls_place()
    {
        var (window, view, vm) = Show(NotesStage.Open, width: 390);

        Assert.True(Shown(view, "EditorLayer"));
        Assert.False(Shown(view, "WallLayer"));
        Assert.True(Shown(view, "BackButton"));
        Assert.False(Shown(view, "CloseButton"));

        vm.CloseEditor();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Shown(view, "WallLayer"));
        window.Close();
    }

    [AvaloniaFact]
    public void Searching_narrows_the_wall_and_says_what_was_found()
    {
        var (window, view, vm) = Show(NotesStage.Wall);

        vm.Search = "nginx";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Nginx 502 khi deploy"], vm.Notes.Select(n => n.Title));
        Assert.StartsWith("1 found", vm.ListSummary);
        Assert.True(vm.IsSearching);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(10, vm.Notes.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void An_empty_wall_points_at_the_first_note()
    {
        var (window, view, vm) = Show(NotesStage.Empty);

        Assert.True(vm.IsListEmpty);
        Assert.Single(Texts(view, "No notes yet. Write your first one."));
        window.Close();
    }

    [AvaloniaFact]
    public void While_Notes_is_off_the_notes_stay_but_cannot_be_written()
    {
        var (window, view, _) = Show(NotesStage.Wall, module: new FakeModule("notes", "Notes") { IsEnabled = false });

        Assert.True(Shown(view, "OffBar"));
        Assert.False(view.FindControl<Button>("NewButton")!.IsEnabled);
        Assert.Single(Texts(view, "Wifi nhà bà ngoại"));
        window.Close();
    }
}
