using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Modules.SlackBot;

/// <summary>The two tokens of the user's Slack app: the user token reads as the user, the app token opens Socket Mode.</summary>
public sealed record SlackTokenPair(string User, string App)
{
    /// <summary>Why these cannot be the right tokens, or null when they look right.</summary>
    public string? Problem() =>
        !User.StartsWith("xoxp-", StringComparison.Ordinal) ? "The user token starts with xoxp- (OAuth & Permissions → User OAuth Token)."
        : !App.StartsWith("xapp-", StringComparison.Ordinal) ? "The app token starts with xapp- (Basic Information → App-Level Tokens)."
        : null;
}

/// <summary>
/// Keeps the tokens on this PC only, encrypted for this Windows user (DPAPI); they never sync and are never shown again.
/// </summary>
public sealed class SlackTokens(HelmPaths paths, ISecretProtector protector)
{
    private const string Purpose = "helm.slackbot.tokens";

    private string FilePath => Path.Combine(paths.ModuleDataDirectory(SlackBotModule.ModuleId), "tokens.bin");

    public bool Exist => File.Exists(FilePath);

    /// <summary>The saved tokens, or null when there are none or they cannot be read here (another user, another PC).</summary>
    public SlackTokenPair? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
            try
            {
                return JsonSerializer.Deserialize<SlackTokenPair>(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Save(SlackTokenPair tokens)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens);
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
