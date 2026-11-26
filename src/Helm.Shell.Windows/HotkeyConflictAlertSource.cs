using System.Globalization;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Today;

namespace Helm.Shell.Windows;

/// <summary>Home's alert when a shortcut is taken: by another app or by two tools of Helm. Its button opens the tool's settings.</summary>
internal sealed class HotkeyConflictAlertSource : IHomeAlertSource
{
    private readonly IHotkeyManager _hotkeys;
    private readonly IModuleHost _modules;
    private readonly IShellWindowsHost _host;

    public HotkeyConflictAlertSource(IHotkeyManager hotkeys, IModuleHost modules, IShellWindowsHost host)
    {
        _hotkeys = hotkeys;
        _modules = modules;
        _host = host;
        hotkeys.ConflictsChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<HomeAlertData> Current() =>
        [.. _hotkeys.Conflicts.Select((c, i) => new HomeAlertData(
            "hotkey:" + i.ToString(CultureInfo.InvariantCulture),
            c.UsedByAnotherApp ? $"{c.Gesture} is taken by another app" : $"{c.Gesture} is used twice",
            c.UsedByAnotherApp ? $"{c.Definitions[0].Description} cannot use it. Pick another shortcut." : c.Describe(),
            "Change",
            5 + i))];

    public Task Act(string alertId)
    {
        var index = int.Parse(alertId["hotkey:".Length..], CultureInfo.InvariantCulture);
        var conflicts = _hotkeys.Conflicts;
        if (index < conflicts.Count && _modules.Find(conflicts[index].Definitions[0].ModuleId) is { } module) _host.OpenPage(module.Id);
        return Task.CompletedTask;
    }
}
