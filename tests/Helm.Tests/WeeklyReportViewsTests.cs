using System.IO;
using System.Windows;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.SlackBot;
using Helm.Modules.WeeklyReport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The Weekly Report pages load, built off-screen (no window is shown). With HELM_WEEKLY_SNAPSHOTS set to a folder each
/// one is rendered to a PNG there, for checking the layout by eye.
/// </summary>
public sealed class WeeklyReportViewsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class NoAgent : ISlackAgent, IReportAgent
    {
        public string? Unavailable() => null;
        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct) => Task.FromResult("Báo cáo");
    }

    private sealed class NoGit : IGitRunner
    {
        public Task<string?> RunAsync(string folder, IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void The_pages_load() => WpfHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var paths = new HelmPaths(_dir.Path);
        using var settings = new SettingsStoreFactory(paths);
        var protector = new PlainSecretProtector();
        using var inbox = new SlackInboxFile(paths, protector, NullLogger<SlackInboxFile>.Instance);
        var agent = new NoAgent();
        var slack = new SlackBotEngine(new SlackTokens(paths, protector), inbox, new SlackSorter(agent), agent, settings, NullLogger<SlackBotEngine>.Instance);
        var history = new ReportHistory(paths, protector);
        history.Save(new SavedReport(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), "Báo cáo công việc tuần 19/10 – 25/10/2026\n• game-x (khoảng 12 giờ)", DateTimeOffset.Now));
        var vm = new WeeklyReportViewModel(new WeekCollector(new GitActivity(new NoGit()), slack, settings), new WeeklyReportWriter(agent), history, slack, settings);
        var module = new WeeklyReportModule { IsEnabled = true };
        Assert.Single(vm.Past);

        Layout(new System.Windows.Controls.Image { Source = WeeklyReportLogo.Image, Width = 128, Height = 128 }, new Size(128, 128), "0-icon");
        Layout(new WeeklyReportContentPage(module, vm), new Size(1000, 900), "1-content");
        Layout(new WeeklyReportPage(new WeeklyReportSettingsViewModel(module, settings, new NoServices())), new Size(1000, 1200), "2-settings");
    });

    private static void Layout(FrameworkElement element, Size size, string name)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        if (Environment.GetEnvironmentVariable("HELM_WEEKLY_SNAPSHOTS") is not { Length: > 0 } folder) return;
        var host = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("ApplicationBackgroundBrush"),
            Width = size.Width,
            Height = size.Height,
        };
        host.Measure(size);
        host.Arrange(new Rect(size));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var png = File.Create(Path.Combine(folder, $"{name}.png"));
        encoder.Save(png);
    }
}
