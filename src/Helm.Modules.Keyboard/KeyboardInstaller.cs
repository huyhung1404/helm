using System.Diagnostics;
using System.Security.Cryptography;
using Helm.Core.Desktop;
using Helm.Typing;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Helm.Modules.Keyboard;

public enum KeyboardInstallState
{
    /// <summary>This build of Helm has no input method next to it (a developer build).</summary>
    NotBundled,
    NotInstalled,
    /// <summary>Installed, but an older version than the one next to Helm.</summary>
    Outdated,
    Installed,
}

/// <param name="OldVersionsInUse">Folders of older versions still loaded by running apps (deleted once they close).</param>
public sealed record KeyboardInstallStatus(KeyboardInstallState State, int OldVersionsInUse);

/// <summary>
/// Puts the Helm Keyboard input method on this PC and takes it off again. The DLLs (64- and 32-bit) ship next to Helm in
/// <c>keyboard\</c>; each version is copied to its own folder under Program Files, because every app that typed with the
/// old one keeps that file open until it closes. Registering needs administrator rights, which Helm runs with.
/// </summary>
public sealed class KeyboardInstaller(IInputMethods inputMethods, ILogger<KeyboardInstaller> logger)
{
    private const string DllName = "HelmKeyboard.dll";

    /// <summary>The plain US keyboard Helm Keyboard replaces under English, and gives back on removal.</summary>
    private const string UsKeyboard = "0409:00000409";

    private static readonly string[] Architectures = ["x64", "x86"];

    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Helm Keyboard");

    private static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "keyboard");

    public KeyboardInstallStatus GetStatus()
    {
        var target = TargetDirectory();
        var installed = RegisteredDll(RegistryView.Registry64);
        var oldInUse = Directory.Exists(InstallRoot)
            ? Directory.GetDirectories(InstallRoot).Count(dir => !SamePath(dir, target) && !SamePath(dir, VersionDirectory(installed)))
            : 0;
        var state = installed is null ? (target is null ? KeyboardInstallState.NotBundled : KeyboardInstallState.NotInstalled)
            : target is not null && !SamePath(VersionDirectory(installed), target) ? KeyboardInstallState.Outdated
            : KeyboardInstallState.Installed;
        return new KeyboardInstallStatus(state, oldInUse);
    }

    /// <summary>Installs (or updates to) the version next to Helm and makes it the English keyboard.</summary>
    public void Install()
    {
        var target = TargetDirectory() ?? throw new InvalidOperationException("This build of Helm does not include Helm Keyboard.");
        foreach (var arch in Architectures)
        {
            var directory = Path.Combine(target, arch);
            Directory.CreateDirectory(directory);
            var source = new FileInfo(Path.Combine(BundledDirectory, arch, DllName));
            var destination = new FileInfo(Path.Combine(directory, DllName));
            // The folder is named by content: a file already there is this one, unless a copy broke off.
            if (!destination.Exists || destination.Length != source.Length) source.CopyTo(destination.FullName, overwrite: true);
        }
        RunRegsvr32(Path.Combine(target, "x64", DllName), unregister: false, wow64: false);
        RunRegsvr32(Path.Combine(target, "x86", DllName), unregister: false, wow64: true);

        if (!inputMethods.AddInputMethod(KeyboardIds.InputMethod, makeDefault: true))
            throw new InvalidOperationException("Windows did not add Helm Keyboard to the keyboard list.");
        inputMethods.RemoveInputMethod(UsKeyboard);
        // Windows' own Ctrl+Shift (switch keyboards within a language) takes the Shift before the shortcut sees it.
        inputMethods.SetLayoutSwitchShortcut(false);
        logger.LogInformation("Helm Keyboard installed from {Directory}", target);
        DeleteOldVersions(keep: target);
    }

    /// <summary>Takes Helm Keyboard off and gives English its US keyboard back.</summary>
    public void Uninstall()
    {
        inputMethods.AddInputMethod(UsKeyboard, makeDefault: true);
        inputMethods.RemoveInputMethod(KeyboardIds.InputMethod);
        inputMethods.SetLayoutSwitchShortcut(true);
        if (RegisteredDll(RegistryView.Registry64) is { } x64) RunRegsvr32(x64, unregister: true, wow64: false);
        if (RegisteredDll(RegistryView.Registry32) is { } x86) RunRegsvr32(x86, unregister: true, wow64: true);
        logger.LogInformation("Helm Keyboard removed");
        DeleteOldVersions(keep: null);
    }

    /// <summary>
    /// For Helm's uninstaller, which may run without administrator rights: the keyboard list is the user's own, but
    /// unregistering asks Windows for elevation once.
    /// </summary>
    public static void RemoveWithHelm(Action<string> log)
    {
        var x64 = RegisteredDll(RegistryView.Registry64);
        var x86 = RegisteredDll(RegistryView.Registry32);
        if (x64 is null && x86 is null) return;
        var inputMethods = new InputMethods();
        inputMethods.AddInputMethod(UsKeyboard, makeDefault: true);
        inputMethods.RemoveInputMethod(KeyboardIds.InputMethod);
        inputMethods.SetLayoutSwitchShortcut(true);
        log("Helm Keyboard taken off the keyboard list.");
        var commands = new List<string>();
        if (x64 is not null) commands.Add($"\"{Regsvr32(wow64: false)}\" /s /u \"{x64}\"");
        if (x86 is not null) commands.Add($"\"{Regsvr32(wow64: true)}\" /s /u \"{x86}\"");
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {string.Join(" & ", commands)}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            log("Helm Keyboard unregistering (elevated).");
        }
        catch (Exception ex)
        {
            log($"Could not unregister Helm Keyboard: {ex.Message}");
        }
    }

    /// <summary>The folder this version installs to, named by version and content (null when none is bundled).</summary>
    private static string? TargetDirectory()
    {
        var x64 = Path.Combine(BundledDirectory, "x64", DllName);
        if (!File.Exists(x64) || !File.Exists(Path.Combine(BundledDirectory, "x86", DllName))) return null;
        var version = FileVersionInfo.GetVersionInfo(x64).FileVersion ?? "0";
        using var stream = File.OpenRead(x64);
        var hash = Convert.ToHexString(SHA256.HashData(stream))[..8].ToLowerInvariant();
        return Path.Combine(InstallRoot, $"{version}-{hash}");
    }

    private static string? RegisteredDll(RegistryView view)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = root.OpenSubKey($@"SOFTWARE\Classes\CLSID\{{{KeyboardIds.Clsid}}}\InprocServer32");
        return key?.GetValue(null) is string path && File.Exists(path) ? path : null;
    }

    /// <summary>"…\Helm Keyboard\&lt;version&gt;" from "…\Helm Keyboard\&lt;version&gt;\x64\HelmKeyboard.dll".</summary>
    private static string? VersionDirectory(string? dll) => dll is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(dll));

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Regsvr32(bool wow64) =>
        Path.Combine(Environment.GetFolderPath(wow64 ? Environment.SpecialFolder.SystemX86 : Environment.SpecialFolder.System), "regsvr32.exe");

    private void RunRegsvr32(string dll, bool unregister, bool wow64)
    {
        var arguments = unregister ? $"/s /u \"{dll}\"" : $"/s \"{dll}\"";
        using var process = Process.Start(new ProcessStartInfo(Regsvr32(wow64), arguments) { UseShellExecute = false, CreateNoWindow = true })
                            ?? throw new InvalidOperationException("regsvr32 did not start.");
        if (!process.WaitForExit(TimeSpan.FromSeconds(30))) throw new TimeoutException("regsvr32 did not finish.");
        if (process.ExitCode != 0)
        {
            logger.LogWarning("regsvr32 {Arguments} exited with {Code}", arguments, process.ExitCode);
            if (!unregister) throw new InvalidOperationException($"Registering Helm Keyboard failed (regsvr32 exit code {process.ExitCode}). Helm needs to run as administrator.");
        }
    }

    /// <summary>Deletes the folders of other versions; one an app still has loaded stays until next time.</summary>
    private void DeleteOldVersions(string? keep)
    {
        if (!Directory.Exists(InstallRoot)) return;
        foreach (var directory in Directory.GetDirectories(InstallRoot))
        {
            if (SamePath(directory, keep)) continue;
            try
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("Deleted old Helm Keyboard {Directory}", directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Helm Keyboard {Directory} is still in use", directory);
            }
        }
    }

    /// <summary>Called when the module starts: an update brings a new version next to Helm, which replaces the old one.</summary>
    public void CleanUp() => DeleteOldVersions(keep: VersionDirectory(RegisteredDll(RegistryView.Registry64)));
}
