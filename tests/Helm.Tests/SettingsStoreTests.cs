using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_yields_defaults_with_current_version()
    {
        using var store = new SettingsStore<SampleSettings>(Path.Combine(_dir, "sample.json"));

        Assert.Equal(SampleSettings.CurrentVersion, store.Current.Version);
        Assert.Equal("default", store.Current.Name);
    }

    [Fact]
    public void Update_then_flush_round_trips_all_fields()
    {
        var path = Path.Combine(_dir, "sample.json");
        using (var store = new SettingsStore<SampleSettings>(path))
        {
            store.Update(s =>
            {
                s.Name = "zones";
                s.Count = 7;
                s.Mode = SampleMode.Second;
                s.Hotkey = new HotkeyGesture(HotkeyModifiers.Win | HotkeyModifiers.Ctrl, 'T');
                s.Excluded.Add("Unity.exe");
            });
            store.Flush();
        }

        using var reloaded = new SettingsStore<SampleSettings>(path);
        Assert.Equal("zones", reloaded.Current.Name);
        Assert.Equal(7, reloaded.Current.Count);
        Assert.Equal(SampleMode.Second, reloaded.Current.Mode);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Win | HotkeyModifiers.Ctrl, 'T'), reloaded.Current.Hotkey);
        Assert.Equal(["Unity.exe"], reloaded.Current.Excluded);
        Assert.Contains("\"mode\": \"second\"", File.ReadAllText(path));
    }

    [Fact]
    public void Update_raises_changed_and_debounces_write()
    {
        var path = Path.Combine(_dir, "sample.json");
        using var store = new SettingsStore<SampleSettings>(path, debounce: TimeSpan.FromMinutes(5));
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Update(s => s.Count = 1);
        store.Update(s => s.Count = 2);

        Assert.Equal(2, raised);
        Assert.False(File.Exists(path)); // still debounced
        store.Flush();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_keeps_backup()
    {
        var path = Path.Combine(_dir, "sample.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");

        using var store = new SettingsStore<SampleSettings>(path);

        Assert.Equal("default", store.Current.Name);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Older_version_is_migrated()
    {
        var path = Path.Combine(_dir, "sample.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{ "version": 1, "name": "old", "count": 3 }""");

        using var store = new SettingsStore<SampleSettings>(path);

        Assert.Equal(SampleSettings.CurrentVersion, store.Current.Version);
        Assert.Equal(30, store.Current.Count); // v1 -> v2 multiplies count by 10
    }

    [Fact]
    public void Suspended_writes_are_dropped()
    {
        var path = Path.Combine(_dir, "sample.json");
        var suspended = false;
        using var store = new SettingsStore<SampleSettings>(path, writesSuspended: () => suspended);

        suspended = true;
        store.Update(s => s.Count = 5);
        store.Flush();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Non_finite_doubles_are_written_and_read_back_instead_of_crashing()
    {
        // Regression: an unset window position (NaN) made the debounced write throw on the thread pool.
        var path = Path.Combine(_dir, "general.json");
        using (var store = new SettingsStore<GeneralSettings>(path))
        {
            store.Update(s =>
            {
                s.Window.Width = double.NaN;
                s.Window.Height = double.PositiveInfinity;
            });
            store.Flush();
        }

        using var reloaded = new SettingsStore<GeneralSettings>(path);
        Assert.True(double.IsNaN(reloaded.Current.Window.Width));
        Assert.True(double.IsPositiveInfinity(reloaded.Current.Window.Height));
        Assert.Null(reloaded.Current.Window.Left);
    }

    [Fact]
    public void Legacy_nan_window_position_loads_as_unset_or_nan_without_throwing()
    {
        var path = Path.Combine(_dir, "general.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{ "version": 1, "window": { "width": 1100, "height": 780 } }""");

        using var store = new SettingsStore<GeneralSettings>(path);

        Assert.Equal(1100, store.Current.Window.Width);
        Assert.Null(store.Current.Window.Top);
    }

    public enum SampleMode { First, Second }

    public sealed class SampleSettings : IVersionedSettings
    {
        public static int CurrentVersion => 2;
        public int Version { get; set; }
        public string Name { get; set; } = "default";
        public int Count { get; set; }
        public SampleMode Mode { get; set; }
        public HotkeyGesture Hotkey { get; set; }
        public List<string> Excluded { get; set; } = [];

        public void Migrate(int fromVersion)
        {
            if (fromVersion < 2) Count *= 10;
        }
    }
}
