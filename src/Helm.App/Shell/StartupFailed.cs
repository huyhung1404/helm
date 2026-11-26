using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Helm.App.Shell;

/// <summary>The one message Helm can show when it could not start at all: what went wrong and where the log is.</summary>
internal static class StartupFailed
{
    /// <summary>Shows the message; done when it is closed.</summary>
    public static Task ShowAsync(string text)
    {
        var closed = new TaskCompletionSource();
        try
        {
            var ok = new Button { Classes = { "pill" }, Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96 };
            var window = new Window
            {
                Title = "Helm",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 16,
                    Children = { new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, ok },
                },
            };
            ok.Click += (_, _) => window.Close();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
        }
        catch (Exception)
        {
            // Not even a window: the log has it.
            closed.TrySetResult();
        }
        return closed.Task;
    }
}
