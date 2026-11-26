using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Shell.BugReports;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.BugReports;

/// <summary>
/// The phone's Bug reports page. Closing and reopening a bug ask their question in a card that
/// opens in place (no dialog over the page).
/// </summary>
public partial class BugReportsView : UserControl
{
    public BugReportsView()
    {
        InitializeComponent();
        var settings = Reporter.Settings;
        ShakeSwitch.IsChecked = settings.Current.ShakeToReport;
        ShakeSwitch.IsCheckedChanged += (_, _) => settings.Update(s => s.ShakeToReport = ShakeSwitch.IsChecked == true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BugReportsViewModel vm)
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BugReportsViewModel.Selected)) HidePanels(); };
        };
    }

    private static AndroidBugReporter Reporter => App.Services.GetRequiredService<AndroidBugReporter>();

    private BugDetailViewModel? Detail => (DataContext as BugReportsViewModel)?.Selected;

    private void Report_Click(object? sender, RoutedEventArgs e) => Reporter.Report();

    private void HidePanels()
    {
        ClosePanel.IsVisible = false;
        ReopenPanel.IsVisible = false;
    }

    private void HidePanels_Click(object? sender, RoutedEventArgs e) => HidePanels();

    private void ShowClose_Click(object? sender, RoutedEventArgs e)
    {
        HidePanels();
        FixedRadio.IsChecked = true;
        DuplicateOf.Text = "";
        CloseNote.Text = "";
        ClosePanel.IsVisible = true;
        ClosePanel.BringIntoView();
    }

    private async void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail) return;
        var reason = NotABugRadio.IsChecked == true ? BugCloseReason.NotABug : DuplicateRadio.IsChecked == true ? BugCloseReason.Duplicate : BugCloseReason.Fixed;
        HidePanels();
        await detail.CloseAsync(reason, Clean(CloseNote.Text), reason == BugCloseReason.Duplicate ? Clean(DuplicateOf.Text) : null);
    }

    private void ShowReopen_Click(object? sender, RoutedEventArgs e)
    {
        HidePanels();
        ReopenWhy.Text = "";
        ReopenWithBox.IsChecked = true;
        ReopenPanel.IsVisible = true;
        ReopenPanel.BringIntoView();
        ReopenWhy.Focus();
    }

    private async void Reopen_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail) return;
        if (Clean(ReopenWhy.Text) is not { } why)
        {
            ReopenWhy.PlaceholderText = "Write why first";
            ReopenWhy.Focus();
            return;
        }
        HidePanels();
        await detail.ReopenAsync(why, ReopenWithBox.IsChecked == true);
    }

    private async void AnswerImage_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail) return;
        foreach (var image in await Reporter.PickAsync(BugStore.MaxImages - detail.AnswerImages.Count)) detail.AddAnswerImage(image);
    }

    private async void DetailsImage_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail) return;
        foreach (var image in await Reporter.PickAsync(BugStore.MaxImages - detail.DetailImages.Count)) detail.AddDetailImage(image);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
