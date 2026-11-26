using Avalonia.Controls;
using Helm.Shell.Pages;

namespace Helm.Gallery;

/// <summary>The shell's small page, Welcome, as the real view.</summary>
internal static class ShellPagesDemo
{
    public static Control Welcome() => new WelcomeView(() => { });
}
