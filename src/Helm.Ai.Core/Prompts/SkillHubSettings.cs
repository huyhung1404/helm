using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Ai.Prompts;

/// <summary>
/// Where this device reads the user's own prompts from (settings/ai-skillhub.json, this device only): the Skill Hub's
/// MCP address and, typed once, its tokens. Helm moves the tokens into <see cref="SkillHubSecrets"/> (encrypted for this
/// user) at the next look and empties them here. No address: nothing is fetched.
/// </summary>
public sealed class SkillHubSettings : IVersionedSettings
{
    public const string StoreId = "ai-skillhub";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>The Skill Hub's MCP endpoint (https://…/api/mcp); empty: off.</summary>
    public string Url { get; set; } = "";

    /// <summary>This machine's Skill Hub token (the site → Machines). Moved out of the file once read.</summary>
    public string Token { get; set; } = "";

    /// <summary>The Cloudflare Access service token in front of the hub, if any. Moved out of the file once read.</summary>
    public string AccessClientId { get; set; } = "";

    public string AccessClientSecret { get; set; } = "";
}

/// <summary>What a request to the Skill Hub needs.</summary>
public sealed record SkillHubConnection(Uri Url, string Token, string? AccessClientId, string? AccessClientSecret);

/// <summary>The tokens a request to the Skill Hub carries, on this device only, encrypted for this user, never synced.</summary>
public sealed class SkillHubSecrets(HelmPaths paths, ISecretProtector protector)
{
    private const string Purpose = "helm.ai.skillhub";

    private sealed record Stored(string Token, string AccessClientId, string AccessClientSecret);

    private string FilePath => Path.Combine(paths.ModuleDataDirectory("ai"), "skillhub.bin");

    /// <summary>
    /// The connection: the address from the settings file and the saved tokens, after moving any token typed into the
    /// file into the encrypted one. Null when there is no https address or no token.
    /// </summary>
    public SkillHubConnection? Connection(ISettingsStore<SkillHubSettings> settings)
    {
        settings.Reload();
        var file = settings.Current;
        var saved = Read();
        if (file.Token.Length > 0 || file.AccessClientId.Length > 0 || file.AccessClientSecret.Length > 0)
        {
            saved = new Stored(Pick(file.Token, saved?.Token), Pick(file.AccessClientId, saved?.AccessClientId), Pick(file.AccessClientSecret, saved?.AccessClientSecret));
            Write(saved);
            settings.Update(s =>
            {
                s.Token = "";
                s.AccessClientId = "";
                s.AccessClientSecret = "";
            });
            settings.Flush();
        }
        if (saved is not { Token.Length: > 0 } || UrlProblem(file.Url) is not null) return null;
        return new SkillHubConnection(new Uri(file.Url.Trim()), saved.Token, NullIfEmpty(saved.AccessClientId), NullIfEmpty(saved.AccessClientSecret));

        static string Pick(string typed, string? kept) => typed.Trim().Length > 0 ? typed.Trim() : kept ?? "";
        static string? NullIfEmpty(string s) => s.Length > 0 ? s : null;
    }

    /// <summary>Why the hub's address cannot be used; null when it can.</summary>
    public static string? UrlProblem(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return "Type the hub's MCP address, like https://skill.example.com/api/mcp.";
        // The tokens travel with every request: never over plain http, except to this PC itself (a hub under test).
        return uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback ? "Use https: the tokens travel with every request." : null;
    }

    /// <summary>Which tokens are saved on this device (never their values): the machine token, the Access service token.</summary>
    public (bool Token, bool Access) Saved()
    {
        var saved = Read();
        return (saved is { Token.Length: > 0 }, saved is { AccessClientId.Length: > 0, AccessClientSecret.Length: > 0 });
    }

    /// <summary>
    /// Saves the tokens typed on the AI page, encrypted, never in the settings file. An empty one keeps what was saved;
    /// <paramref name="clearAccess"/> drops the Access service token (a hub without Cloudflare Access in front).
    /// </summary>
    public void Save(string token, string accessClientId, string accessClientSecret, bool clearAccess = false)
    {
        var saved = Read();
        var id = clearAccess ? "" : Pick(accessClientId, saved?.AccessClientId);
        var secret = clearAccess ? "" : Pick(accessClientSecret, saved?.AccessClientSecret);
        Write(new Stored(Pick(token, saved?.Token), id, secret));

        static string Pick(string typed, string? kept) => typed.Trim().Length > 0 ? typed.Trim() : kept ?? "";
    }

    private Stored? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
            try
            {
                return JsonSerializer.Deserialize<Stored>(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            // Unreadable here (another user, another PC): as if none; typing the tokens again replaces it.
            return null;
        }
    }

    private void Write(Stored stored)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored);
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
}
