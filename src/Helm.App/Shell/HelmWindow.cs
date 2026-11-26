using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Helm.Shell.Views;

namespace Helm.App.Shell;

/// <summary>
/// A Helm window: Helm's own title bar and window buttons (the <see cref="ShellView"/> draws them and tags them with
/// their roles, so Windows still drags, snaps and maximizes), Helm's background across it, the app icon. The main
/// window and each torn-off tab window are one of these.
/// </summary>
internal class HelmWindow : Window
{
    public HelmWindow(ShellView content)
    {
        Content = content;
        Title = "Helm";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Helm/Assets/helm.ico")));
        MinWidth = 400;
        MinHeight = 500;
        // Windows keeps the border (resize, snap, shadow) and draws no title bar of its own: Helm's is the shell's.
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 48;
        Background = Brushes.Transparent;
    }

    public ShellView Shell => (ShellView)Content!;
}
