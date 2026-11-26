using System.Windows;
using System.Windows.Controls;
using Helm.Shell.BugReports;

namespace Helm.App.BugReports;

internal partial class BugReportsPage : Page
{
    private readonly BugReportsViewModel _viewModel;
    private readonly PcBugReporter _reporter;

    public BugReportsPage(BugReportsViewModel viewModel, PcBugReporter reporter)
    {
        _viewModel = viewModel;
        _reporter = reporter;
        DataContext = viewModel;
        InitializeComponent();
    }

    private BugDetailViewModel? Detail => _viewModel.Selected;

    private void Report_Click(object sender, RoutedEventArgs e) => _reporter.Report();

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail) return;
        if (await BugDialogs.AskCloseAsync(detail.Code).ConfigureAwait(true) is { } answer)
            await detail.CloseAsync(answer.Reason, answer.Note, answer.DuplicateOf).ConfigureAwait(true);
    }

    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail) return;
        if (await BugDialogs.AskReopenAsync(detail.Code).ConfigureAwait(true) is { } answer)
            await detail.ReopenAsync(answer.Why, answer.WithBlackBox).ConfigureAwait(true);
    }

    private void ShowBlackBox_Click(object sender, RoutedEventArgs e)
    {
        BlackBoxExpander.IsExpanded = true;
        BlackBoxExpander.BringIntoView();
    }

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BugImageItem image) return;
        try
        {
            await BugImage.OpenAsync(image).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception or Helm.Core.Sync.BlobUnavailableException or System.Net.Http.HttpRequestException)
        {
            if (Detail is { } detail) detail.Status = $"This picture cannot be opened now: {ex.Message}";
        }
    }

    private void AddDetailImage_Click(object sender, RoutedEventArgs e)
    {
        foreach (var image in BugImage.Pick(Window.GetWindow(this))) Detail?.AddDetailImage(image);
    }

    private void AddAnswerImage_Click(object sender, RoutedEventArgs e)
    {
        foreach (var image in BugImage.Pick(Window.GetWindow(this))) Detail?.AddAnswerImage(image);
    }

    private void RemovePending_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BugImageItem image || Detail is not { } detail) return;
        if (!detail.DetailImages.Remove(image)) detail.AnswerImages.Remove(image);
    }
}
