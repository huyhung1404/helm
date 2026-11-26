using Avalonia.Data.Converters;
using Helm.Shell.Services;

namespace Helm.Modules.Ssh.Ui;

/// <summary>Small formats for the SSH page.</summary>
public static class SshText
{
    /// <summary>A server's tile: the first letter of its name.</summary>
    public static IValueConverter Initial { get; } = new FuncValueConverter<string, string>(title =>
        string.IsNullOrWhiteSpace(title) ? "?" : title.Trim()[..1].ToUpperInvariant());
}

/// <summary>
/// The page's questions, with the terminal out of their way while one is open: the terminal is a native web view, drawn
/// over everything Avalonia draws, so a sheet over it would be hidden behind it.
/// </summary>
internal sealed class TerminalSafeDialogs(IDialogService inner, Action<bool> asking) : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        asking(true);
        try
        {
            return await inner.ConfirmAsync(title, message, confirmText);
        }
        finally
        {
            asking(false);
        }
    }
}
