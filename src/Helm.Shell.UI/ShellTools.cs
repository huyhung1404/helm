using Avalonia.Media;
using Helm.Core.Modules;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Home;

/// <summary>Each tool's own picture, as the menu draws it; null for a tool without one (a Fluent glyph is drawn instead).</summary>
public interface IToolIcons
{
    IImage? For(string moduleId);
}

/// <summary>
/// Helm's tools as Home and General show them: one <see cref="HomeTool"/> for each module, its switch kept in step with
/// the module's own on/off in both directions. One instance serves both pages.
/// </summary>
public sealed class ShellTools
{
    public ShellTools(IEnumerable<IModule> modules, IToolIcons icons)
    {
        var tools = new List<HomeTool>();
        foreach (var module in modules)
        {
            var tool = new HomeTool(module.Id, module.DisplayName, module.Group.DisplayName(), Symbol.Apps, icons.For(module.Id), module.IsEnabled);
            var source = module;
            tool.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HomeTool.IsEnabled) && source.IsEnabled != tool.IsEnabled) source.IsEnabled = tool.IsEnabled;
            };
            module.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IModule.IsEnabled) && tool.IsEnabled != source.IsEnabled) tool.IsEnabled = source.IsEnabled;
            };
            tools.Add(tool);
        }
        All = tools;
    }

    public IReadOnlyList<HomeTool> All { get; }
}
