using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Helm.Shell.Services;

namespace Helm.Core.Ui;

/// <summary>
/// Helm's confirmation dialog for every platform: a <see cref="Sheet"/> with the question, what will happen, and
/// Cancel / the action (a pill). A tap outside or Escape is Cancel. Give it any control on the page it should open over.
/// </summary>
public sealed class GlassDialogs(Func<Control> anchor) : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SheetHandle? handle = null;

        var cancel = new Button { Classes = { "ghost" }, Content = "Cancel", MinWidth = 96 };
        var ok = new Button { Classes = { "pill" }, Content = confirmText, MinWidth = 112 };
        cancel.Click += (_, _) => handle?.Close();
        ok.Click += (_, _) =>
        {
            result.TrySetResult(true);
            handle?.Close();
        };
        var body = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = message, Classes = { "secondary" }, FontSize = 14, TextWrapping = TextWrapping.Wrap },
                    },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        handle = Sheet.Show(anchor(), body, 380);
        if (handle is null) return Task.FromResult(false);
        handle.Closed += () => result.TrySetResult(false);
        return result.Task;
    }
}
