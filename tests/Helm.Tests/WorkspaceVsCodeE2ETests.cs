using Helm.Core.Desktop;
using Helm.Core.Settings;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// Runs only on a desk set up for it: VS Code windows with Helm Bridge whose <c>HELM_DATA_DIR</c> is
/// <c>HELM_WORKSPACE_E2E_DATA</c>, signed in to Claude Code. It types into those windows with the real keyboard, so
/// nobody should be typing on that PC meanwhile.
/// <list type="bullet">
/// <item><c>HELM_WORKSPACE_E2E_HOOKS</c>: the Claude settings files to put the hooks in (';'-separated, e.g. each
/// test folder's <c>.claude\settings.json</c>; never your own user settings).</item>
/// <item><c>HELM_WORKSPACE_E2E_FOLDER</c>: the folder of the window where a new session is started.</item>
/// <item><c>HELM_WORKSPACE_E2E_CONTINUE</c> (optional): the title of an earlier session to send a second prompt to.</item>
/// </list>
/// </summary>
public sealed class VsCodeFactAttribute : FactAttribute
{
    public VsCodeFactAttribute()
    {
        if (WorkspaceVsCodeE2ETests.Data is null || WorkspaceVsCodeE2ETests.Folder is null || WorkspaceVsCodeE2ETests.HookFiles.Count == 0)
            Skip = "Set HELM_WORKSPACE_E2E_DATA, HELM_WORKSPACE_E2E_HOOKS and HELM_WORKSPACE_E2E_FOLDER to type into real VS Code windows.";
    }
}

public sealed class WorkspaceVsCodeE2ETests
{
    internal static readonly string? Data = Environment.GetEnvironmentVariable("HELM_WORKSPACE_E2E_DATA") is { Length: > 0 } d ? d : null;
    internal static readonly string? Folder = Environment.GetEnvironmentVariable("HELM_WORKSPACE_E2E_FOLDER") is { Length: > 0 } f ? f : null;
    internal static readonly string? Continue = Environment.GetEnvironmentVariable("HELM_WORKSPACE_E2E_CONTINUE") is { Length: > 0 } c ? c : null;
    internal static readonly IReadOnlyList<string> HookFiles =
        (Environment.GetEnvironmentVariable("HELM_WORKSPACE_E2E_HOOKS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [VsCodeFact]
    public async Task Starts_a_session_and_continues_one_in_real_vs_code()
    {
        var dir = Directory.CreateTempSubdirectory("helm-ws-e2e-");
        using var factory = new SettingsStoreFactory(new HelmPaths(dir.FullName));
        var settings = factory.Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        var sender = new PromptSender(new WindowsPromptKeyboard(new WindowService(NullLogger<WindowService>.Instance)), NullLogger<PromptSender>.Instance);
        await using var engine = WorkspaceEngine.Start(settings, new HelmPaths(dir.FullName), sender, NullLogger.Instance,
            bridgePipe: WorkspaceEndpoint.PipeName(new HelmPaths(Data!)));
        try
        {
            foreach (var file in HookFiles) Assert.Null(engine.InstallClaudeHooks(file));

            // Bridges retry every 5 s after their Helm went away.
            var window = await WorkspaceBridgeTests.WaitLongAsync(() => engine.Windows().FirstOrDefault(w => w.Connection.Info.Folders.Any(f => WorkspaceEngine.IsUnder(Folder!, f))));
            var token = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

            var started = await engine.RunAsync(new WorkspaceCommand
            {
                MachineId = engine.MachineId,
                Kind = WorkspaceCommandKind.NewSession,
                Folder = Folder!,
                Model = "haiku",
                Text = $"Reply with exactly one line: NEW-{token}",
                CreatedAt = DateTimeOffset.Now,
            }, CancellationToken.None);
            Assert.True(started.Status == WorkspaceCommandStatus.Received, started.Error);
            var done = await WorkspaceBridgeTests.WaitLongAsync(() => engine.Registry.Find(started.SessionId!) is { State: SessionState.Done } s ? s : null);
            Assert.Contains($"NEW-{token}", done.LastReply);

            if (Continue is not null)
            {
                var earlier = engine.Registry.Sessions.FirstOrDefault(s => s.Title == Continue);
                Assert.True(earlier is not null, $"No session titled “{Continue}” among: {string.Join(", ", engine.Registry.Sessions.Select(s => s.Title))}");
                var next = await engine.RunAsync(new WorkspaceCommand
                {
                    MachineId = engine.MachineId,
                    SessionId = earlier.SessionId,
                    Text = $"Trả lời đúng một dòng: AGAIN-{token}\nDòng hai có dấu.",
                    CreatedAt = DateTimeOffset.Now,
                }, CancellationToken.None);
                Assert.True(next.Status == WorkspaceCommandStatus.Received, next.Error);
                var again = await WorkspaceBridgeTests.WaitLongAsync(() =>
                    engine.Registry.Find(earlier.SessionId) is { State: SessionState.Done, LastReply: { } r } s && r.Contains($"AGAIN-{token}") ? s : null);
                Assert.Equal(earlier.SessionId, again.SessionId);
            }
        }
        finally
        {
            foreach (var file in HookFiles) WorkspaceEngine.RemoveClaudeHooks(NullLogger.Instance, file);
            factory.FlushAll();
        }
    }
}
