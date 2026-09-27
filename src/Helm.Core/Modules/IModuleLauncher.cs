namespace Helm.Core.Modules;

/// <summary>
/// A module's "do it now" action for its Quick access tile on Home (e.g. start a new chat), instead of just
/// opening its settings page. Register it next to the module; modules without one keep opening their page.
/// </summary>
public interface IModuleLauncher
{
    /// <summary>The <see cref="IHelmModule.Id"/> this launcher belongs to.</summary>
    string ModuleId { get; }

    /// <summary>Runs on the UI thread. Must not throw; report problems in the module's own UI.</summary>
    Task LaunchAsync();
}
