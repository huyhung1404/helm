using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Helm.Core.Capture;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.QuickCapture;
using Helm.Modules.QuickCapture.Ui;

namespace Helm.Gallery;

/// <summary>Which Quick Capture a Gallery entry shows.</summary>
internal enum QuickCaptureStage
{
    /// <summary>The page: the switch, the shortcut, where text goes, what to type.</summary>
    Page,
    /// <summary>The shortcut taken by another app.</summary>
    ShortcutTaken,
    /// <summary>The capture box with a task typed.</summary>
    Box,
    /// <summary>The capture box with no tool turned on.</summary>
    BoxNoTargets,
}

/// <summary>The real Quick Capture views and view models over settings in a temporary folder and pretend targets.</summary>
internal static class QuickCaptureDemo
{
    public static Control Create(FakeModule module, QuickCaptureStage stage) => stage switch
    {
        QuickCaptureStage.Box or QuickCaptureStage.BoxNoTargets => Box(module, stage).View,
        _ => Build(module, stage).View,
    };

    /// <summary>The page with its view model and pretend tool (the UI tests drive them).</summary>
    public static (QuickCaptureView View, QuickCaptureViewModel ViewModel, PretendQuickCapture Tool) Build(FakeModule module, QuickCaptureStage stage)
    {
        var tool = new PretendQuickCapture(module, Settings());
        if (stage == QuickCaptureStage.ShortcutTaken)
            tool.StatusMessage = "The shortcut Win+Alt+N is not active: another app uses it. Pick other keys below.";
        var vm = new QuickCaptureViewModel(tool);
        return (new QuickCaptureView(vm, new Host()), vm, tool);
    }

    /// <summary>The capture box, as it floats over another app (centred on the Gallery's page).</summary>
    public static (Control View, CaptureBoxView Box, CaptureBoxViewModel ViewModel, List<string> Saved) Box(FakeModule module, QuickCaptureStage stage)
    {
        var saved = new List<string>();
        IReadOnlyList<ICaptureTarget> targets = stage == QuickCaptureStage.BoxNoTargets ? [] : Targets(saved);
        var vm = new CaptureBoxViewModel(() => targets, Settings().Get<QuickCaptureSettings>("quick-capture"));
        var box = new CaptureBoxView(vm) { Width = 640, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        box.Prepare();
        if (stage == QuickCaptureStage.Box) vm.Text = "/t Gọi cho anh Minh về hợp đồng tomorrow 9h !";
        var page = new Panel { Children = { new Helm.Core.Ui.Backdrop(), box } };
        return (page, box, vm, saved);
    }

    private static SettingsStoreFactory Settings() =>
        new(new HelmPaths(Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"))));

    private static List<ICaptureTarget> Targets(List<string> saved) =>
    [
        new PretendTarget("note", "notes", "Note", "n", "an idea, a link, a quote", 0, saved),
        new PretendTarget("task", "tracker", "Task", "t", "buy milk tomorrow 9h #Home !", 1, saved),
        new PretendTarget("debt", "wallet", "Debt", "d", "Minh owes 200k for lunch", 2, saved),
        new PretendTarget("word", "vocab", "Word", "v", "serendipity", 3, saved),
    ];

    private sealed class Host : IQuickCapturePageHost;

    private sealed class PretendTarget(string id, string moduleId, string name, string prefix, string example, int order, List<string> saved) : ICaptureTarget
    {
        public string Id => id;
        public string ModuleId => moduleId;
        public string Name => name;
        public string Prefix => prefix;
        public string Example => example;
        public int Order => order;

        public CapturePreview Preview(string text) =>
            id == "task" ? new(true, $"Task in Inbox · \"{Shorten(text)}\" · due tomorrow 09:00 · High") : new(true, $"{name}: \"{Shorten(text)}\"");

        public CaptureResult Capture(string text)
        {
            saved.Add($"{id}: {text}");
            return new(true, $"{name} saved.");
        }

        private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;
    }
}

/// <summary>Quick Capture, pretended: "Open now" is only counted. On/off follows the Gallery's switch.</summary>
internal sealed class PretendQuickCapture : IQuickCapture
{
    private readonly FakeModule _module;
    private string? _status;

    public PretendQuickCapture(FakeModule module, ISettingsStoreFactory settings)
    {
        _module = module;
        Settings = settings.Get<QuickCaptureSettings>("quick-capture");
        module.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e);
    }

    public string Id => "quick-capture";
    public string DisplayName => "Quick Capture";
    public string Description => "";
    public ModuleGroup Group => ModuleGroup.Planning;

    public bool IsEnabled
    {
        get => _module.IsEnabled;
        set => _module.IsEnabled = value;
    }

    public string? StatusMessage
    {
        get => _status;
        set
        {
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
        }
    }

    public ISettingsStore<QuickCaptureSettings> Settings { get; }

    public IReadOnlyList<ICaptureTarget> AllTargets { get; } =
    [
        new Target("note", "Note", "n", "an idea, a link, a quote", 0),
        new Target("task", "Task", "t", "buy milk tomorrow 9h #Home !", 1),
        new Target("debt", "Debt", "d", "Minh owes 200k for lunch", 2),
    ];

    /// <summary>How often "Open Quick Capture now" was pressed (the UI tests read it).</summary>
    public int Opened { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Open() => Opened++;

    public Task EnableAsync(CancellationToken ct) => Task.CompletedTask;

    public Task DisableAsync() => Task.CompletedTask;

    private sealed class Target(string id, string name, string prefix, string example, int order) : ICaptureTarget
    {
        public string Id => id;
        public string ModuleId => id;
        public string Name => name;
        public string Prefix => prefix;
        public string Example => example;
        public int Order => order;

        public CapturePreview Preview(string text) => new(true, text);

        public CaptureResult Capture(string text) => new(true, "Saved.");
    }
}
