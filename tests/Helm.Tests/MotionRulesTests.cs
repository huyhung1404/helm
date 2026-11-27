using System.Text.RegularExpressions;

namespace Helm.Tests;

/// <summary>
/// Helm's motion rules (helm-ui-ux skill §6), checked in the source so a new animation cannot make a page lag: loops run
/// on the compositor (core:Motion.Spin / Pulse), never as a style animation that repeats on the UI thread; nothing
/// animates a size or a margin (the whole page would be laid out every frame); drawn motion uses MotionClock, never a
/// fast DispatcherTimer.
/// </summary>
public sealed partial class MotionRulesTests
{
    private static readonly string Src = Path.Combine(Root(), "src");

    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Helm.sln"))) root = root.Parent;
        return root!.FullName;
    }

    private static IEnumerable<(string File, int Line, string Text)> Lines(string pattern) =>
        Directory.EnumerateFiles(Src, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((text, i) => (Path.GetRelativePath(Src, f), i + 1, text)));

    [Fact]
    public void No_style_animation_loops_forever()
    {
        var found = Lines("*.axaml").Where(l => l.Text.Contains("IterationCount=\"INFINITE\"", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(found.Count == 0, "Use core:Motion.Spin or core:Motion.Pulse: " + string.Join("; ", found.Select(f => $"{f.File}:{f.Line}")));
    }

    // The menu's own surface may still grow (it floats over the page, which is not laid out again).
    private static readonly string[] SizeAllowed = [Path.Combine("Helm.Shell.UI", "Shell", "NavMenuView.axaml"), Path.Combine("Helm.Shell.UI", "Shell", "NavMenuView.axaml.cs")];

    [GeneratedRegex(@"Transition\s+Property=""(Width|Height|MinWidth|MinHeight|MaxWidth|MaxHeight|Margin|Padding)""|Property\s*=\s*(Width|Height|MinWidth|MinHeight|MaxWidth|MaxHeight|Margin|Padding)Property")]
    private static partial Regex SizeTransition();

    [Fact]
    public void Nothing_animates_a_size_or_a_margin()
    {
        var found = Lines("*.axaml").Concat(Lines("*.cs"))
            .Where(l => SizeTransition().IsMatch(l.Text) && (l.Text.Contains("Transition") || l.Text.Contains("KeyFrame")))
            .Where(l => !SizeAllowed.Contains(l.File)).ToList();
        Assert.True(found.Count == 0, "Lay out once and slide (Motion.SlideFrom) or scale instead: " + string.Join("; ", found.Select(f => $"{f.File}:{f.Line}")));
    }

    [GeneratedRegex(@"new DispatcherTimer\(TimeSpan\.FromMilliseconds\((\d+)\)")]
    private static partial Regex FastTimer();

    [Fact]
    public void Drawn_motion_uses_the_motion_clock()
    {
        var found = Lines("*.cs").Where(l => FastTimer().Match(l.Text) is { Success: true } m && int.Parse(m.Groups[1].Value) < 100).ToList();
        Assert.True(found.Count == 0, "Use MotionClock.Run: " + string.Join("; ", found.Select(f => $"{f.File}:{f.Line}")));
    }
}
