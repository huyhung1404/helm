using System.Security.Cryptography;
using System.Text;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Ai.Vps;

/// <summary>
/// Where this PC serves the user's VPS from (settings/ai-vps.json): on this PC only, never synced, so two PCs never
/// both answer with one token unless the user sets both up.
/// </summary>
public sealed class VpsWorkerSettings : IVersionedSettings
{
    public const string StoreId = "ai-vps";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>The VPS's worker endpoint (wss://host/ai-worker); empty until the user adds it.</summary>
    public string Url { get; set; } = "";

    public bool Enabled { get; set; }
}

public static class VpsUrls
{
    /// <summary>
    /// The endpoint as a WebSocket address, or why it is not one. Only wss:// (the token travels in it); ws:// only to
    /// this PC itself, for a server under test.
    /// </summary>
    public static Uri? Check(string text, out string? problem)
    {
        problem = null;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) || url.Host.Length == 0)
            problem = "Type the whole address, e.g. wss://your-vps/ai-worker.";
        else if (url.Scheme == "ws" && !url.IsLoopback)
            problem = "Use wss:// (encrypted): ws:// only reaches this PC itself.";
        else if (url.Scheme is not ("wss" or "ws"))
            problem = "The address starts with wss://.";
        return problem is null ? url : null;
    }
}

/// <summary>
/// The token the VPS knows this worker by. It lets anyone who has it send jobs to the user's agents, so it stays on this
/// PC, encrypted for this user (DPAPI), never synced and never shown again once saved.
/// </summary>
public sealed class VpsWorkerToken(HelmPaths paths, ISecretProtector protector)
{
    private const string Purpose = "helm.ai.vps-worker";

    private string FilePath => Path.Combine(paths.ModuleDataDirectory("ai"), "vps-worker-token.bin");

    public bool Has => File.Exists(FilePath);

    /// <summary>The saved token, or null when there is none or it cannot be read here (another user, another PC).</summary>
    public string? Get()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token.Trim());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, protector.Protect(bytes, Purpose));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
