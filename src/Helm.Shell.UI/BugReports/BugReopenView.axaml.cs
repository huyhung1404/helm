using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Helm.BugReports.Ui;

/// <summary>What "Reopen" answered: why, and whether the black box goes with it.</summary>
public sealed record BugReopenAnswer(string Why, bool WithBlackBox);

/// <summary>The "Reopen #B12?" sheet's body; <paramref name="done"/> gets the answer, or null for Cancel.</summary>
public partial class BugReopenView : UserControl
{
    private readonly Action<BugReopenAnswer?>? _done;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public BugReopenView() => InitializeComponent();

    /// <param name="stillBroken">From "Still broken" on a fix to check (else "Reopen" on a closed bug).</param>
    public BugReopenView(string code, bool stillBroken, Action<BugReopenAnswer?> done)
    {
        _done = done;
        InitializeComponent();
        Heading.Text = stillBroken ? $"{code} is still broken?" : $"Reopen {code}?";
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Why.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Writes the reason, as typing does.</summary>
    public string? Text
    {
        get => Why.Text;
        set => Why.Text = value;
    }

    private void Why_TextChanged(object? sender, TextChangedEventArgs e) => ReopenButton.IsEnabled = BugCloseView.Clean(Why.Text) is not null;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _done?.Invoke(null);

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (BugCloseView.Clean(Why.Text) is { } why) _done?.Invoke(new BugReopenAnswer(why, WithBox.IsChecked == true));
    }
}
