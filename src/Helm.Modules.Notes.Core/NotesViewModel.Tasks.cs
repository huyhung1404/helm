using CommunityToolkit.Mvvm.Input;
using Helm.Core.Settings;

namespace Helm.Modules.Notes;

// "Make tasks": the open note's tasks into the to-do lists (the panel is NoteTasksViewModel).
public sealed partial class NotesViewModel
{
    private readonly ISettingsStore<GeneralSettings> _general;

    /// <summary>The "Make tasks" panel of the open note; null without a to-do tool (Tracker) in the app.</summary>
    public NoteTasksViewModel? Tasks { get; }

    /// <summary>Notes offer "Make tasks": AI is set up and the to-do tool is there and on.</summary>
    public bool OffersTasks
    {
        get
        {
            if (Tasks is not { IsAvailable: true }) return false;
            var enabled = _general.Current.EnabledModules;
            return !enabled.TryGetValue(Tasks.ModuleId, out var on) || on;
        }
    }

    /// <summary>The open note offers "Make tasks" (not one in the trash).</summary>
    public bool CanMakeTasks => IsEditorOpen && !EditTrashed && OffersTasks;

    /// <summary>The toolbar's or the menu's "Make tasks": saves the note, then AI proposes its tasks in the panel.</summary>
    [RelayCommand]
    private Task MakeTasksAsync()
    {
        Flush();
        if (!CanMakeTasks || Tasks is null) return Task.CompletedTask;
        if (EditTitle.Trim().Length == 0 && EditBody.Trim().Length == 0)
        {
            Message = "Write something first: AI looks for the tasks in the note's text.";
            return Task.CompletedTask;
        }
        return Tasks.StartAsync();
    }

    /// <summary>A card's "Make tasks" (right-click on PC): opens the note, then as the toolbar's.</summary>
    [RelayCommand]
    private Task MakeTasksRowAsync(NoteRowViewModel? row)
    {
        if (row is null || row.Trashed) return Task.CompletedTask;
        Open(row.Id);
        return MakeTasksAsync();
    }
}
