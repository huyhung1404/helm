using Avalonia.Controls;

namespace Helm.Modules.Ssh.Ui;

/// <summary>
/// What each platform does its own way around the shared SSH page. The terminal is one: xterm.js in a web view (WebView2
/// on Windows, Android's WebView), shown in the page through a NativeControlHost. Members a platform lacks have a default,
/// and the page leaves out what it would offer.
/// </summary>
public interface ISshPageHost
{
    /// <summary>The page came on screen, or the app came back to it.</summary>
    void Opened() { }

    /// <summary>
    /// The terminal for the page's lifetime: it shows <see cref="SshViewModel.ActiveTerminal"/> (the shell, or the AI agent
    /// tab), sends what is typed through <see cref="SshViewModel.Send(string)"/> and reports its size
    /// (<see cref="SshViewModel.ReportTerminalSize"/>).
    /// </summary>
    ISshTerminal CreateTerminal(SshViewModel viewModel);

    /// <summary>Where OpenSSH keeps its config, keys and known_hosts (the PC's %USERPROFILE%\.ssh); null: no "Import from SSH config".</summary>
    string? OpenSshFolder => null;

    /// <summary>Asks for a private key file (the path only; it is read when connecting). Null when cancelled or not on this platform.</summary>
    Task<string?> PickKeyFileAsync() => Task.FromResult<string?>(null);

    /// <summary>Key files on this device can be used (the PC); the phone signs in with its own key, a password or Vault.</summary>
    bool CanUseKeyFiles => false;

    /// <summary>One sentence: how this device's private key is kept ("encrypted for your Windows account", "the Android Keystore").</summary>
    string KeyProtection => "Its private half is encrypted on this device and never leaves it.";

    /// <summary>Settings only this platform has, as cards in the settings sheet. Null: none.</summary>
    Control? CreatePlatformSettings() => null;

    /// <summary>A phone's page fills the screen and has no app bar: this opens its menu (the drawer). Null: no menu button.</summary>
    Action<Control>? OpenMenu => null;
}

/// <summary>The platform's terminal in the page.</summary>
public interface ISshTerminal
{
    /// <summary>What the page puts in its terminal area (a NativeControlHost around the web view).</summary>
    Control View { get; }

    /// <summary>Gives the typing focus to the terminal (and opens a phone's keyboard).</summary>
    void Focus();

    /// <summary>A key of the key bar, by name: esc, tab, up, down, left, right, home, end.</summary>
    void SendKey(string key);

    /// <summary>The clipboard's text into the terminal (bracketed when the shell asks for it).</summary>
    Task PasteAsync();
}
