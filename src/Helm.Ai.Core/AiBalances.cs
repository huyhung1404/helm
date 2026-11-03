using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Ai;

/// <summary>What a provider says is left of the prepaid credit, or why it could not be read.</summary>
/// <param name="Usd">Null when it could not be read.</param>
public sealed record AiBalance(DateTimeOffset At, decimal? Usd, string Message);

/// <summary>
/// Reads the prepaid balance of a provider whose API tells it. It needs a key with more power than an API key (xAI: a
/// management key), so that key stays on one device (<see cref="AiManagementKeys"/>).
/// </summary>
public interface IAiBalanceReader
{
    AiProvider Provider { get; }

    /// <param name="account">The account the balance belongs to (xAI: the team id an API key's test found).</param>
    Task<AiBalance> ReadAsync(string managementKey, string account, CancellationToken ct);
}

/// <summary>xAI's Management API: the team's prepaid balance, in USD cents (purchases negative, spending positive).</summary>
public sealed class GrokBalanceReader(HttpClient http, TimeProvider? time = null) : IAiBalanceReader
{
    internal const string BaseUrl = "https://management-api.x.ai/v1/";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AiProvider Provider => AiProviders.Grok;

    public async Task<AiBalance> ReadAsync(string managementKey, string account, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}billing/teams/{Uri.EscapeDataString(account)}/prepaid/balance";
            var (status, json) = await AiHttp.GetAsync(http, url, managementKey.Trim(), ct);
            if (status is 401 or 403)
                return Failed("xAI does not accept this management key, or the key may not read billing.");
            if (status == 404) return Failed("xAI does not know the team of your Grok key. Test the key again.");
            if (status >= 500) return Failed("xAI's servers had a problem. Try again later.");
            if (status != 200 || json is not { } body || !body.TryGetProperty("total", out var total)
                || total.Text("val") is not { } val || !decimal.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cents))
                return Failed($"xAI answered {status} without a balance.");
            // The ledger counts what the team owes: a purchase is negative, so what is left is its opposite.
            var usd = -cents / 100m;
            return new AiBalance(_time.GetUtcNow(), usd, usd <= 0 ? "No prepaid credit left: Grok answers nothing until you buy more." : "Prepaid credit left.");
        }
        catch (HttpRequestException)
        {
            return Failed("Could not reach xAI. Check the internet connection.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("xAI did not answer in time.");
        }
        catch (JsonException)
        {
            return Failed("xAI answered something Helm could not read.");
        }

        AiBalance Failed(string message) => new(_time.GetUtcNow(), null, message);
    }
}

/// <summary>
/// The management keys that read balances. They can do more than read (an xAI one can buy credit with the saved card), so
/// they stay on this device only, encrypted for this user (DPAPI, Android Keystore), never synced and never shown again.
/// </summary>
public sealed class AiManagementKeys(HelmPaths paths, ISecretProtector protector)
{
    private string FilePath(AiProvider provider) => Path.Combine(paths.ModuleDataDirectory("ai"), $"management-{provider.Id}.bin");

    private static string Purpose(AiProvider provider) => $"helm.ai.management.{provider.Id}";

    public bool Has(AiProvider provider) => File.Exists(FilePath(provider));

    /// <summary>The saved key, or null when there is none or it cannot be read here (another user, another device).</summary>
    public string? Get(AiProvider provider)
    {
        try
        {
            var path = FilePath(provider);
            if (!File.Exists(path)) return null;
            var bytes = protector.Unprotect(File.ReadAllBytes(path), Purpose(provider));
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

    public void Save(AiProvider provider, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key.Trim());
        try
        {
            var path = FilePath(provider);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protector.Protect(bytes, Purpose(provider)));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(AiProvider provider)
    {
        var path = FilePath(provider);
        if (File.Exists(path)) File.Delete(path);
    }
}
