using System.Windows;
using System.Windows.Controls;
using Helm.Shell.BugReports;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Helm.App.BugReports;

/// <summary>The small dialogs of bug reports, in WPF-UI's message box like Helm's other questions.</summary>
internal static class BugDialogs
{
    public sealed record CloseAnswer(BugCloseReason Reason, string? Note, string? DuplicateOf);

    public sealed record ReopenAnswer(string Why, bool WithBlackBox);

    public static async Task<CloseAnswer?> AskCloseAsync(string code)
    {
        var fixedOption = Radio("Fixed", "You checked: it no longer happens", true);
        var notABug = Radio("Not a bug", "Helm works as designed", false);
        var duplicate = Radio("Duplicate", "Another bug says the same", false);
        var original = new TextBox { PlaceholderText = "The original bug, e.g. #B7", Margin = new Thickness(28, 0, 0, 8), Visibility = Visibility.Collapsed };
        duplicate.Checked += (_, _) => original.Visibility = Visibility.Visible;
        duplicate.Unchecked += (_, _) => original.Visibility = Visibility.Collapsed;
        var note = new TextBox { PlaceholderText = "Note (optional)", Margin = new Thickness(0, 4, 0, 0) };
        var panel = Stack(fixedOption, notABug, duplicate, original, note);
        if (!await AskAsync($"Close {code}", panel, "Close bug").ConfigureAwait(true)) return null;
        var reason = notABug.IsChecked == true ? BugCloseReason.NotABug : duplicate.IsChecked == true ? BugCloseReason.Duplicate : BugCloseReason.Fixed;
        return new CloseAnswer(reason, Text(note), reason == BugCloseReason.Duplicate ? Text(original) : null);
    }

    public static async Task<ReopenAnswer?> AskReopenAsync(string code)
    {
        var why = new TextBox { PlaceholderText = "What still happens", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
        var withBox = new CheckBox { Content = "Attach the black box (last 60 s)", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        var panel = Stack(new TextBlock { Text = "Why is it not fixed?", Margin = new Thickness(0, 0, 0, 6) }, why, withBox);
        while (true)
        {
            if (!await AskAsync($"Reopen {code}", panel, "Reopen").ConfigureAwait(true)) return null;
            if (Text(why) is { } text) return new ReopenAnswer(text, withBox.IsChecked == true);
            why.PlaceholderText = "Write why first";
        }
    }

    private static async Task<bool> AskAsync(string title, UIElement content, string confirm) =>
        await new WpfUiMessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            MinWidth = 460,
        }.ShowDialogAsync().ConfigureAwait(true) == WpfUiMessageBoxResult.Primary;

    private static RadioButton Radio(string title, string detail, bool isChecked)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title });
        var sub = new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(sub);
        return new RadioButton { Content = text, IsChecked = isChecked, GroupName = "bug-dialog", Margin = new Thickness(0, 2, 0, 6) };
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { MinWidth = 400 };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    private static string? Text(System.Windows.Controls.TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
}
