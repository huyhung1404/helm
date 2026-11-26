using Avalonia.Threading;
using Helm.App.Shell;
using Helm.Core.Desktop;
using Helm.Core.Diagnostics;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.BugReports;
using Helm.Shell.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.App.BugReports;

/// <summary>
/// Bug reports on the PC: the report window from anywhere (Ctrl+Alt+F12, the title bar, the tray), the black box's
/// hooks, the report a crash left, and tray notifications when an agent asks something or a fix arrives with an update.
/// </summary>
internal sealed class PcBugReporter(
    IServiceProvider services,
    BugStore store,
    BlackBox box,
    IHotkeyManager hotkeys,
    IModuleHost modules,
    AppShellHost shell,
    IWindowService windows,
    IMonitorService monitors,
    TrayService tray,
    HelmPaths paths,
    ILogger<PcBugReporter> logger) : IPcBugReporting
{
    /// <summary>Ctrl+Alt+F12 (Win+Alt+B is Windows' HDR switch, Ctrl+Alt+B is VS Code's).</summary>
    public static readonly HotkeyGesture Hotkey = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x7B);

    private readonly HashSet<string> _notifiedQuestions = [];
    private readonly HashSet<string> _notifiedFixes = [];
    private BugReportPanel? _window;
    private BlackBoxSnapshot? _crash;
    private int _checkQueued;

    /// <summary>Hooks the black box and listens (call once, on the UI thread, after the host started).</summary>
    public async Task StartAsync()
    {
        PcBlackBoxHooks.Install(box, () => shell.Shell.Tabs.ActivePageTitle);
        BugReportsServices.StartBlackBox(services, () => shell.Shell.Tabs.ActivePageTitle);
        tray.ReportBug = () => Dispatcher.UIThread.Post(() => Report());

        foreach (var bug in store.All().Where(b => b.Value.PendingQuestion is not null)) _notifiedQuestions.Add(bug.Value.PendingQuestion!);
        store.Changed += (_, _) => QueueCheck();

        _crash = BlackBox.TakeCrash(paths.CrashBlackBoxFile);
        if (_crash is not null)
            tray.Show("Helm closed unexpectedly", "Its black box was kept. Click to report the crash.", () => Report(_crash, fromCrash: true));
        Check();

        var registration = await hotkeys.TryRegisterAsync(new HotkeyDefinition("helm", "report-bug", "Report a bug", Hotkey),
            () => Dispatcher.UIThread.Post(() => Report())).ConfigureAwait(true);
        if (!registration.IsRegistered) logger.LogWarning("Ctrl+Alt+F12 for bug reports is taken: {Error}", registration.Error);
    }

    /// <summary>Freezes the black box and opens the report window (or brings it up when it is open).</summary>
    public void Report(BlackBoxSnapshot? crash = null, bool fromCrash = false)
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return;
        }
        PcBlackBoxHooks.Flush();
        // Freeze in every case: the window's own clicks stay out of the box until it closes (the form thaws it).
        var frozen = box.Freeze();
        var snapshot = crash ?? frozen;
        if (fromCrash) _crash = null;
        var tools = modules.Modules.Select(m => m.DisplayName).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        var page = shell.Shell.Tabs.ActivePageTitle;
        var tool = fromCrash ? null : tools.FirstOrDefault(t => page is not null && page.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        var form = new BugReportFormViewModel(store, box, snapshot, tool, tools, fromCrash);
        try
        {
            _window = new BugReportPanel(form, windows, monitors, id => ShowPage(id));
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        catch (Exception ex)
        {
            // The shared form could not open: the black box must not stay frozen.
            logger.LogError(ex, "The report window could not open");
            form.Close();
            _window = null;
            tray.Show("Could not open the report window", ex.GetBaseException().Message, null);
        }
    }

    /// <inheritdoc />
    public string ReportHint => "Ctrl+Alt+F12, anywhere";

    void IPcBugReporting.Report() => Report();

    /// <summary>The Bug reports page in the main window, with <paramref name="id"/> opened.</summary>
    public void ShowPage(string? id = null)
    {
        services.GetRequiredService<ShellController>().ShowMainWindow();
        services.GetRequiredService<IShellNavigation>().ShowTool(Helm.Shell.Tabs.ShellPages.BugReports);
        if (id is not null) services.GetRequiredService<BugReportsViewModel>().Open(id);
    }

    private void QueueCheck()
    {
        if (Interlocked.Exchange(ref _checkQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _checkQueued, 0);
            Check();
        });
    }

    /// <summary>A question from an agent, or a fix this Helm now has: one tray notification each.</summary>
    private void Check()
    {
        foreach (var bug in store.All())
        {
            if (bug.Value.PendingQuestion is { } question && _notifiedQuestions.Add(question))
            {
                var text = store.GetEntry(question)?.Text ?? "";
                tray.Show($"{bug.Value.FixingBy ?? "Claude Code"} asks about #{bug.Value.Code}", text, () => ShowPage(bug.Id));
            }
        }
        foreach (var bug in store.FixedIn(AppInfo.Version))
        {
            if (!_notifiedFixes.Add(bug.Id)) continue;
            tray.Show($"Is bug #{bug.Value.Code} fixed?", $"Helm {AppInfo.Version} has a fix for \"{bug.Value.Title}\". Click to check it.", () => ShowPage(bug.Id));
        }
    }
}
