using System.Text.RegularExpressions;
using Wpf.Ui.Controls;

namespace Helm.Tests;

/// <summary>
/// WPF-UI 4.3 draws a SymbolRegular as one 16-bit char, so a symbol above U+FFFF shows a stray letter instead of the
/// icon. Every symbol the PC app and its tools name must fit in 16 bits.
/// </summary>
public sealed partial class VaultSymbolTests
{
    [Fact]
    public void Every_symbol_the_PC_app_and_tools_use_is_drawn_as_an_icon()
    {
        // Every WPF project (the Android ones use FluentIcons, which has no such limit).
        var roots = Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src"))
            .Where(d => !d.EndsWith(".Android", StringComparison.Ordinal) && Directory.EnumerateFiles(d, "*.xaml", SearchOption.AllDirectories).Any());
        var names = roots.SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => SymbolName().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value + m.Groups[2].Value))
            .Distinct()
            .ToList();
        Assert.NotEmpty(names);

        var wide = names.Where(n => !Enum.TryParse<SymbolRegular>(n, out var s) || (int)s > 0xFFFF).ToList();
        Assert.True(wide.Count == 0, $"Unknown or above U+FFFF: {string.Join(", ", wide)}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Helm.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Helm.sln not found above the test folder");
    }

    // SymbolRegular.Name24 in C#, {ui:SymbolIcon Name24} and Symbol="Name24" in XAML.
    [GeneratedRegex(@"SymbolRegular\.([A-Za-z0-9]+)|(?:SymbolIcon |Symbol="")([A-Za-z0-9]+\d)")]
    private static partial Regex SymbolName();
}
