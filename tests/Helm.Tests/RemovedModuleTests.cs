using Helm.Core.Modules;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// A tool that was removed (Claude Chat, id "claude-chat") leaves its enabled state in general.json and its own
/// settings and data on disk. Helm starts quietly, ignores them, and never deletes them.
/// </summary>
public sealed class RemovedModuleTests
{
    private const string RemovedId = "claude-chat";

    [Fact]
    public async Task An_old_entry_for_a_removed_tool_is_ignored_and_kept()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        Directory.CreateDirectory(paths.SettingsDirectory);
        File.WriteAllText(paths.SettingsFile(GeneralSettings.StoreId),
            """{ "version": 1, "enabledModules": { "claude-chat": true, "notes": false } }""");
        File.WriteAllText(paths.SettingsFile(RemovedId), """{ "version": 1, "hotkey": "Ctrl+Alt+C" }""");
        Directory.CreateDirectory(paths.ModuleDataDirectory(RemovedId));
        var history = Path.Combine(paths.ModuleDataDirectory(RemovedId), "history.json");
        File.WriteAllText(history, "[]");

        var notes = new FakeModule("notes");
        var tracker = new FakeModule("tracker");
        using (var settings = new SettingsStoreFactory(paths))
        {
            var registry = new ModuleRegistry<IModule>([notes, tracker], settings, NullLogger.Instance);
            await registry.StartAsync(CancellationToken.None);

            Assert.Null(registry.Find(RemovedId));
            Assert.Equal(2, registry.Modules.Count);
            Assert.False(notes.IsEnabled);
            Assert.Equal(0, notes.Enabled);
            Assert.True(tracker.IsEnabled);
            Assert.Equal(1, tracker.Enabled);

            // A toggle rewrites general.json; the old entry is carried along, not dropped.
            notes.IsEnabled = true;
            Assert.Equal(1, notes.Enabled);
        }

        using (var reread = new SettingsStoreFactory(paths))
        {
            var general = reread.Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            Assert.True(general.EnabledModules[RemovedId]);
            Assert.True(general.EnabledModules["notes"]);
        }
        Assert.Contains("Ctrl+Alt+C", File.ReadAllText(paths.SettingsFile(RemovedId)));
        Assert.Equal("[]", File.ReadAllText(history));
    }

    private sealed class FakeModule(string id) : ModuleBase
    {
        public int Enabled { get; private set; }

        public override string Id => id;
        public override string DisplayName => id;
        public override string Description => "";
        public override ModuleGroup Group => ModuleGroup.Planning;

        public override Task EnableAsync(CancellationToken ct)
        {
            Enabled++;
            return Task.CompletedTask;
        }

        public override Task DisableAsync() => Task.CompletedTask;
    }
}
