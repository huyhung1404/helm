using Helm.Core.Modules;
using Helm.General;
using Helm.Shell.Home;

namespace Helm.Shell.Windows;

/// <summary>Where a tap on Home goes on Windows: the tool's own action, else its content page, else its settings.</summary>
internal sealed class WindowsShellLinks(IModuleHost modules, IEnumerable<IModuleLauncher> launchers, IShellWindowsHost host, GeneralSelection general) : IHomeLinks
{
    public void OpenTool(string moduleId)
    {
        var launcher = launchers.FirstOrDefault(l => l.ModuleId == moduleId);
        if (launcher is not null)
        {
            _ = launcher.LaunchAsync();
            return;
        }
        if (modules.Find(moduleId) is not { } module) return;
        host.OpenPage((module as IModuleContent)?.ContentPageType ?? module.SettingsPageType);
    }

    public void OpenGeneral(string part)
    {
        general.Select(part);
        host.OpenGeneral();
    }

    public void OpenWhatsNew() => host.OpenWhatsNew();
}

/// <summary>Which part of General to show when the page opens (an alert's button picks it).</summary>
internal sealed class GeneralSelection
{
    public GeneralViewModel? Model { get; set; }

    public void Select(string part)
    {
        if (Model is { } model && model.Sections.FirstOrDefault(s => s.Id == part) is { } section) model.Selected = section;
    }
}
