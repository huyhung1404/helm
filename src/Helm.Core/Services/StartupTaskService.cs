using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;

namespace Helm.Core.Services;

public interface IStartupTaskService
{
    bool IsEnabled();

    /// <summary>Creates/updates the "Helm" logon task (highest privileges → no UAC prompt at logon).</summary>
    Task EnableAsync(string executablePath, string arguments = "--startup");

    Task DisableAsync();
}

/// <summary>Auto-start through Task Scheduler so the elevated app can launch at logon without a UAC prompt.</summary>
public sealed class StartupTaskService(ILogger<StartupTaskService> logger) : IStartupTaskService
{
    public const string TaskName = "Helm";

    public bool IsEnabled()
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(TaskName);
            return task is { Enabled: true };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not query startup task");
            return false;
        }
    }

    public Task EnableAsync(string executablePath, string arguments = "--startup") => Task.Run(() =>
    {
        using var ts = new TaskService();
        var user = WindowsIdentity.GetCurrent().Name;

        var td = ts.NewTask();
        td.RegistrationInfo.Description = "Starts Helm when you sign in.";
        td.RegistrationInfo.Author = user;
        td.Principal.UserId = user;
        td.Principal.LogonType = TaskLogonType.InteractiveToken;
        td.Principal.RunLevel = TaskRunLevel.Highest;
        td.Triggers.Add(new LogonTrigger { UserId = user, Delay = TimeSpan.FromSeconds(3) });
        td.Actions.Add(new ExecAction(executablePath, arguments, Path.GetDirectoryName(executablePath)));
        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries = false;
        td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        td.Settings.Priority = System.Diagnostics.ProcessPriorityClass.Normal;

        ts.RootFolder.RegisterTaskDefinition(TaskName, td, TaskCreation.CreateOrUpdate, user, null, TaskLogonType.InteractiveToken);
        logger.LogInformation("Startup task registered for {Path}", executablePath);
    });

    public Task DisableAsync() => Task.Run(() =>
    {
        using var ts = new TaskService();
        if (ts.GetTask(TaskName) is null) return;
        ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
        logger.LogInformation("Startup task removed");
    });
}
