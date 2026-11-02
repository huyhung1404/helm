using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.WeeklyReport;

public static class WeeklyReportServices
{
    public static IServiceCollection AddWeeklyReportModule(this IServiceCollection services) =>
        services
            .AddSingleton<IGitRunner, GitRunner>()
            .AddSingleton<GitActivity>()
            .AddSingleton<WeekCollector>()
            .AddSingleton<IReportAgent, ClaudeCodeReportAgent>()
            .AddSingleton<WeeklyReportWriter>()
            .AddSingleton<ReportHistory>()
            .AddSingleton<WeeklyReportViewModel>()
            .AddHelmModule<WeeklyReportModule, WeeklyReportPage, WeeklyReportSettingsViewModel>()
            .AddSingleton<WeeklyReportContentPage>();
}
