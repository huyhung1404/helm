using Helm.Core.Settings;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// 2026-10-20: a test copy of Helm wrote its own port into Claude's settings and quit; the installed Helm never heard
/// from Claude again (no "Claude received it", sessions stuck on Working) and nothing said why.
/// </summary>
public sealed class ClaudeHooksGuardTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-hooks-guard-");
    private string File_ => Path.Combine(_dir.FullName, "settings.json");

    private ClaudeHooksGuard Guard(int port, TimeProvider? time = null, bool watch = true) => new(File_,
        json => ClaudeHookConfig.IsInstalled(json, port, Token),
        () =>
        {
            ClaudeHookConfig.Rewrite(File_, json => ClaudeHookConfig.Install(json, port, Token));
            return null;
        }, NullLogger.Instance, time, watch);

    private void TakenOver() => File.WriteAllText(File_, ClaudeHookConfig.Install(File.ReadAllText(File_), 2222, "another-copy-token-another-copy-token"));

    [Fact]
    public async Task Hooks_another_copy_wrote_over_are_put_back_at_once()
    {
        File.WriteAllText(File_, ClaudeHookConfig.Install("""{"theme":"dark"}""", 1111, Token));
        using var guard = Guard(1111);
        string? restored = "not yet";
        guard.Restored += p => restored = p;

        TakenOver();
        await WorkspaceBridgeTests.WaitAsync(() => ClaudeHookConfig.IsInstalled(File.ReadAllText(File_), 1111, Token) ? (object)true : null);
        Assert.Null(restored);
        Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(File_));
    }

    [Fact]
    public void Hooks_in_place_are_left_alone_and_a_program_that_keeps_taking_them_is_not_fought()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        File.WriteAllText(File_, ClaudeHookConfig.Install(null, 1111, Token));
        using var guard = Guard(1111, time, watch: false);
        Assert.False(guard.Check());

        TakenOver();
        Assert.True(guard.Check());
        Assert.True(ClaudeHookConfig.IsInstalled(File.ReadAllText(File_), 1111, Token));

        // Taken out again at once: put back only after a pause.
        TakenOver();
        Assert.False(guard.Check());
        time.Advance(ClaudeHooksGuard.MinGap);
        Assert.True(guard.Check());
    }

    [Fact]
    public void A_copy_on_a_test_data_folder_is_not_the_installed_helm()
    {
        Assert.True(new HelmPaths().IsDefault);
        Assert.True(new HelmPaths(HelmPaths.DefaultRoot + Path.DirectorySeparatorChar).IsDefault);
        Assert.False(new HelmPaths(_dir.FullName).IsDefault);
    }

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }
}
