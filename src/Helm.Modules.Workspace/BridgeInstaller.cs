using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// Installs Helm Bridge (Bridge/ in this project, embedded) into VS Code: builds a .vsix and runs VS Code's own
/// <c>code --install-extension</c>, as the signed-in user (not elevated, like VS Code itself). Skipped when this
/// version is installed already.
/// </summary>
public sealed class BridgeInstaller(IChildProcessLauncher launcher, ILogger<BridgeInstaller> logger)
{
    public const string ExtensionId = "helm.helm-bridge";

    /// <summary>The bridge version shipped with this Helm (from Bridge/package.json).</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>VS Code's command-line launcher (code.cmd), user or system install; null when VS Code is missing.</summary>
    /// <remarks>Not <c>code</c> from PATH: on many machines that is Cursor or another fork.</remarks>
    public static string? FindVsCode()
    {
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code"),
        ];
        return roots.Select(r => Path.Combine(r, "bin", "code.cmd")).FirstOrDefault(File.Exists);
    }

    /// <summary>Development and tests: a separate VS Code (its extensions and user data folders) instead of yours.</summary>
    internal static string? TestExtensionsDir => Environment.GetEnvironmentVariable("HELM_VSCODE_EXTENSIONS_DIR");
    private static string? TestUserDataDir => Environment.GetEnvironmentVariable("HELM_VSCODE_USER_DATA_DIR");

    /// <summary>True when this bridge version sits in the user's VS Code extensions folder.</summary>
    public static bool IsInstalled(string? extensionsDir = null)
    {
        var dir = extensionsDir ?? TestExtensionsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        return Directory.Exists(Path.Combine(dir, $"{ExtensionId}-{Version}"));
    }

    /// <summary>Installs the bridge unless it is there already. Returns null on success, else why it failed.</summary>
    public async Task<string?> EnsureInstalledAsync(CancellationToken ct)
    {
        if (IsInstalled()) return null;
        var code = FindVsCode();
        if (code is null) return "VS Code is not installed (Microsoft VS Code was not found).";
        var vsix = Path.Combine(Path.GetTempPath(), $"helm-bridge-{Version}.vsix");
        try
        {
            await File.WriteAllBytesAsync(vsix, BuildVsix(), ct).ConfigureAwait(false);
            var command = "cmd.exe /d /c " + CommandLine.Quote(code) + " --install-extension " + CommandLine.Quote(vsix) + " --force";
            if (TestExtensionsDir is { } extensions) command += " --extensions-dir " + CommandLine.Quote(extensions);
            if (TestUserDataDir is { } userData) command += " --user-data-dir " + CommandLine.Quote(userData);
            var info = new ChildProcessStartInfo(command, Path.GetTempPath());
            using var process = launcher.Start(info);
            process.StandardInput.Close();
            var output = ReadAllAsync(process.StandardOutput);
            var error = ReadAllAsync(process.StandardError);
            var exit = await process.Exited.WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            if (exit == 0)
            {
                logger.LogInformation("Helm Bridge {Version} installed into VS Code", Version);
                return null;
            }
            var detail = (await error.ConfigureAwait(false)).Trim();
            if (detail.Length == 0) detail = (await output.ConfigureAwait(false)).Trim();
            logger.LogWarning("Installing Helm Bridge failed ({Exit}): {Detail}", exit, detail);
            return "VS Code could not install Helm Bridge: " + (detail.Length > 0 ? detail : $"exit code {exit}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ChildProcessException or TimeoutException)
        {
            logger.LogWarning(ex, "Installing Helm Bridge failed");
            return "Helm Bridge could not be installed: " + ex.Message;
        }
        finally
        {
            try { File.Delete(vsix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A VS Code extension package (an OPC zip) with the embedded bridge files.</summary>
    internal static byte[] BuildVsix()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension=".json" ContentType="application/json" />
                  <Default Extension=".js" ContentType="application/javascript" />
                  <Default Extension=".vsixmanifest" ContentType="text/xml" />
                </Types>
                """);
            Add(zip, "extension.vsixmanifest",
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
                  <Metadata>
                    <Identity Language="en-US" Id="helm-bridge" Version="{Version}" Publisher="helm" />
                    <DisplayName>Helm Bridge</DisplayName>
                    <Description xml:space="preserve">Lets Helm's Workspace Controller type into Claude Code sessions.</Description>
                  </Metadata>
                  <Installation>
                    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
                  </Installation>
                  <Dependencies />
                  <Assets>
                    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
                  </Assets>
                </PackageManifest>
                """);
            Add(zip, "extension/package.json", Resource("package.json"));
            Add(zip, "extension/extension.js", Resource("extension.js"));
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes);
    }

    internal static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Helm.Modules.Workspace.Bridge." + name)
            ?? throw new InvalidOperationException("Missing bridge resource " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReadVersion()
    {
        using var doc = JsonDocument.Parse(Resource("package.json"));
        return doc.RootElement.GetProperty("version").GetString() ?? "0.0.0";
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
