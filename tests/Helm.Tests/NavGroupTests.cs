using System.Reflection;
using System.Runtime.CompilerServices;
using Helm.Core.Modules;

namespace Helm.Tests;

/// <summary>Nav groups say what their tools are for, and every PC tool sits in one.</summary>
public class NavGroupTests
{
    /// <summary>Where each PC tool belongs. A tool not listed here fails the test, so a new one picks its group on purpose.</summary>
    private static readonly Dictionary<string, ModuleGroup> Expected = new()
    {
        ["notes"] = ModuleGroup.Planning,
        ["tracker"] = ModuleGroup.Planning,
        ["missions"] = ModuleGroup.Planning,
        ["quick-capture"] = ModuleGroup.Planning,
        ["wallet"] = ModuleGroup.MoneyAndMedia,
        ["watch-later"] = ModuleGroup.MoneyAndMedia,
        ["scratch"] = ModuleGroup.MoneyAndMedia,
        ["vault"] = ModuleGroup.SecurityAndServers,
        ["ssh"] = ModuleGroup.SecurityAndServers,
        ["always-on-top"] = ModuleGroup.WindowsAndDesktop,
        ["command-palette"] = ModuleGroup.WindowsAndDesktop,
    };

    /// <summary>Tools on their way out; they only need some group.</summary>
    private static readonly HashSet<string> Leaving = ["claude-chat"];

    private static readonly string[] Generic = ["System Tools", "Advanced", "Tools", "Other", "Misc", "More", "General", "Utilities"];

    /// <summary>
    /// Every PC tool in the Helm.Modules.* assemblies next to the tests. The getters read are expression-bodied
    /// constants, so the modules are not constructed (their services are not needed).
    /// </summary>
    private static List<IHelmModule> PcModules()
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "Helm.Modules.*.dll")
            .Select(f => Assembly.Load(AssemblyName.GetAssemblyName(f)));
        return assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IHelmModule).IsAssignableFrom(t))
            .Select(t => (IHelmModule)RuntimeHelpers.GetUninitializedObject(t))
            .ToList();
    }

    [Fact]
    public void Every_pc_tool_has_its_group()
    {
        var modules = PcModules();
        Assert.Subset(modules.Select(m => m.Id).ToHashSet(), Expected.Keys.ToHashSet());
        foreach (var module in modules)
        {
            Assert.True(Enum.IsDefined(module.Group), $"{module.Id}: group {module.Group} is not a ModuleGroup");
            Assert.False(string.IsNullOrWhiteSpace(module.Group.DisplayName()), module.Id);
            if (Leaving.Contains(module.Id)) continue;
            Assert.True(Expected.TryGetValue(module.Id, out var group), $"{module.Id}: add it to NavGroupTests.Expected");
            Assert.Equal(group, module.Group);
        }
    }

    [Fact]
    public void Groups_have_names_that_say_what_is_inside()
    {
        foreach (var group in Enum.GetValues<ModuleGroup>())
        {
            var name = group.DisplayName();
            Assert.DoesNotContain(name, Generic, StringComparer.OrdinalIgnoreCase);
        }
        Assert.Equal(["Planning", "Money & Media", "Security & Servers", "Windows & Desktop"],
            Enum.GetValues<ModuleGroup>().Select(g => g.DisplayName()));
    }

    /// <summary>
    /// The groups were renamed (System Tools / Advanced are gone). That is safe only while no saved data holds a
    /// group: if this fails, a setting or record now stores a <see cref="ModuleGroup"/> and old values need a mapping.
    /// </summary>
    [Fact]
    public void No_saved_data_holds_a_group()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var helm = AppDomain.CurrentDomain.GetAssemblies()
            .Concat(PcModules().Select(m => m.GetType().Assembly))
            .Where(a => a.GetName().Name?.StartsWith("Helm.", StringComparison.Ordinal) == true && a.GetName().Name != "Helm.Tests")
            .Distinct();
        var holders = helm
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsEnum && !typeof(IModule).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(all).Select(p => (t, p.Name, Type: p.PropertyType))
                .Concat(t.GetFields(all).Select(f => (t, f.Name, Type: f.FieldType))))
            .Where(m => m.Type == typeof(ModuleGroup) || Nullable.GetUnderlyingType(m.Type) == typeof(ModuleGroup))
            .Select(m => $"{m.t.FullName}.{m.Name}")
            .ToList();
        Assert.Empty(holders);
    }
}
