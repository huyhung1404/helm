using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>What "Close this bug" answered: why, a note, and the original of a duplicate.</summary>
public sealed record BugCloseAnswer(BugCloseReason Reason, string? Note, string? DuplicateOf);

/// <summary>The "Close #B12?" sheet's body; <paramref name="done"/> gets the answer, or null for Cancel.</summary>
public partial class BugCloseView : UserControl
{
    private readonly Action<BugCloseAnswer?>? _done;
    private BugCloseReason _reason = BugCloseReason.Fixed;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public BugCloseView() => InitializeComponent();

    public BugCloseView(string code, Action<BugCloseAnswer?> done)
    {
        _done = done;
        InitializeComponent();
        Heading.Text = $"Close {code}?";
    }

    public BugCloseReason Reason => _reason;

    /// <summary>Picks a reason, as tapping it does.</summary>
    public void Choose(BugCloseReason reason)
    {
        _reason = reason;
        FixedOption.Classes.Set("selected", reason == BugCloseReason.Fixed);
        NotABugOption.Classes.Set("selected", reason == BugCloseReason.NotABug);
        DuplicateOption.Classes.Set("selected", reason == BugCloseReason.Duplicate);
        var duplicate = reason == BugCloseReason.Duplicate;
        if (duplicate && !OriginalRow.IsVisible) Dispatcher.UIThread.Post(() => Original.Focus(), DispatcherPriority.Input);
        OriginalRow.IsVisible = duplicate;
    }

    private void Reason_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag && Enum.TryParse<BugCloseReason>(tag, out var reason)) Choose(reason);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _done?.Invoke(null);

    private void Confirm_Click(object? sender, RoutedEventArgs e) =>
        _done?.Invoke(new BugCloseAnswer(_reason, Clean(Note.Text), _reason == BugCloseReason.Duplicate ? Clean(Original.Text) : null));

    internal static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
