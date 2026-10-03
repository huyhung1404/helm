namespace Helm.Core.Tasks;

/// <summary>
/// Another tool's to-do list, for a tool that hands work over to it (Missions sends a step to Tracker) without
/// referencing it. Registered by the tool that keeps the tasks (<c>services.AddSingleton&lt;ITaskBridge, …&gt;()</c>);
/// callers use it only while <see cref="ModuleId"/> is on. Callable from any thread; never throws for a missing task.
/// </summary>
public interface ITaskBridge
{
    /// <summary>The tool that keeps the tasks, e.g. "tracker".</summary>
    string ModuleId { get; }

    /// <summary>What the buttons call it, e.g. "Tracker".</summary>
    string Name { get; }

    /// <summary>Adds a task (to the first to-do list, made if there is none); its id, or null when it cannot.</summary>
    string? Add(string title, string notes, DateOnly? due);

    /// <summary>True when the task is done, false while it is open, null when it is gone.</summary>
    bool? IsDone(string id);

    /// <summary>Marks the task done; false when it is gone or done already.</summary>
    bool Complete(string id);

    /// <summary>Raised after any change to the tasks (on any thread).</summary>
    event EventHandler? Changed;
}
