using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Shell.BugReports;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.BugReports;

/// <summary>
/// The phone's Bug reports page. Closing, reopening and handing a bug to Claude Code ask their question in a card that
/// opens in place (no dialog over the page).
/// </summary>
public partial class BugReportsView : UserControl
{
    private IReadOnlyList<HandOffTarget> _targets = [];
    private readonly List<RadioButton> _targetButtons = [];

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
        HandOffPanel.IsVisible = false;
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

    private async void ShowHandOff_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail) return;
        HidePanels();
        HandOffTargets.Children.Clear();
        _targetButtons.Clear();
        HandOffNote.Text = "Looking for your PCs…";
        HandOffSend.IsEnabled = false;
        HandOffPanel.IsVisible = true;
        HandOffPanel.BringIntoView();
        try
        {
            _targets = detail.CanHandOff ? await detail.HandOffTargetsAsync() : [];
        }
        catch (Exception ex) when (ex is Helm.Core.Mcp.McpToolException or InvalidOperationException)
        {
            _targets = [];
            HandOffNote.Text = ex.Message;
            return;
        }
        if (_targets.Count == 0)
        {
            HandOffNote.Text = "No Claude Code session to hand it to: turn on Workspace Controller on a PC with VS Code open (Helm running). Or use Copy for Claude Code.";
            return;
        }
        for (var i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = t.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var sub = new TextBlock { Text = t.Busy ? $"{t.Detail} · busy with another task" : t.Detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            sub.Classes.Add("secondary");
            text.Children.Add(sub);
            var radio = new RadioButton { Content = text, GroupName = "handoff", IsChecked = i == 0 };
            _targetButtons.Add(radio);
            HandOffTargets.Children.Add(radio);
        }
        HandOffNote.Text = detail.HandOffPrompt;
        HandOffSend.IsEnabled = true;
    }

    private async void HandOff_Click(object? sender, RoutedEventArgs e)
    {
        if (Detail is not { } detail || _targets.Count == 0) return;
        var index = Math.Max(0, _targetButtons.FindIndex(r => r.IsChecked == true));
        HidePanels();
        await detail.HandOffAsync(_targets[index]);
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
