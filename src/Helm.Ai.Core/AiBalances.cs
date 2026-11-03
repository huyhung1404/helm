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
/// management key), kept in <see cref="AiManagementKeys"/>.
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

/// <summary>One provider's management key, synced (end-to-end encrypted) like the API keys, so every device reads the balance.</summary>
public sealed record AiManagementKey
{
    /// <summary><see cref="AiProvider.Id"/>.</summary>
    public required string Provider { get; init; }

    public required string Secret { get; init; }

    public DateTimeOffset SavedAt { get; init; }
}

/// <summary>
/// The management keys that read balances, one per provider, on Helm Sync like the API keys (the user's choice: every
/// device shows the balance). They can do more than read (an xAI one can buy credit with the saved card), so they are
/// never shown again once saved. A key an older Helm kept on this device only (DPAPI) moves to sync, but only after the
/// first pull: a newer key another device saved is never overwritten by it. Until then this device still uses it.
/// </summary>
public sealed class AiManagementKeys
{
    public const string Collection = "ai.management";

    private readonly ISyncedCollection<AiManagementKey> _keys;
    private readonly HelmPaths? _paths;
    private readonly ISecretProtector? _protector;
    private readonly ISyncService? _sync;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    /// <summary>Old keys read from this device's files, waiting for the first pull: provider id → key and its file's time.</summary>
    private readonly Dictionary<string, (string Secret, DateTimeOffset SavedAt)> _waiting = new(StringComparer.Ordinal);

    /// <param name="sync">Sync, to wait for its first pull before moving an old key; null (or not set up): moved at once.</param>
    public AiManagementKeys(ISyncedCollection<AiManagementKey> keys, HelmPaths? paths = null, ISecretProtector? protector = null, TimeProvider? time = null,
        ISyncService? sync = null)
    {
        _keys = keys;
        _paths = paths;
        _protector = protector;
        _sync = sync;
        _time = time ?? TimeProvider.System;
        _keys.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        ReadOldKeys();
        if (_waiting.Count == 0) return;
        if (sync is null || sync.Status.State == SyncState.NotConfigured || sync.Status.LastSyncedAt is not null) MoveOldKeys();
        else sync.StatusChanged += OnSynced;
    }

    /// <summary>Raised after a local change and after another device's change arrives (then on a thread-pool thread).</summary>
    public event EventHandler? Changed;

    public bool Has(AiProvider provider) => Get(provider) is not null;

    /// <summary>The saved key, or null when there is none.</summary>
    public string? Get(AiProvider provider)
    {
        if (_keys.Get(provider.Id)?.Secret is { } synced) return synced;
        lock (_gate) return _waiting.TryGetValue(provider.Id, out var old) ? old.Secret : null;
    }

    public void Save(AiProvider provider, string key)
    {
        Forget(provider);
        _keys.Upsert(provider.Id, new AiManagementKey { Provider = provider.Id, Secret = key.Trim(), SavedAt = _time.GetUtcNow() });
    }

    public void Delete(AiProvider provider)
    {
        Forget(provider);
        _keys.Delete(provider.Id);
    }

    private string OldFile(AiProvider provider) => Path.Combine(_paths!.ModuleDataDirectory("ai"), $"management-{provider.Id}.bin");

    private void ReadOldKeys()
    {
        if (_paths is null || _protector is null) return;
        foreach (var provider in AiProviders.All)
        {
            var path = OldFile(provider);
            try
            {
                if (!File.Exists(path)) continue;
                var bytes = _protector.Unprotect(File.ReadAllBytes(path), $"helm.ai.management.{provider.Id}");
                try
                {
                    _waiting[provider.Id] = (Encoding.UTF8.GetString(bytes).Trim(), new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // Unreadable here (another user): it stays; the user adds the key again.
            }
        }
    }

    private void OnSynced(object? sender, SyncStatus status)
    {
        if (status.LastSyncedAt is null || status.State == SyncState.Syncing) return;
        _sync!.StatusChanged -= OnSynced;
        MoveOldKeys();
    }

    /// <summary>Each old key goes to sync unless a key is there already (the user's newer one), then its file goes.</summary>
    private void MoveOldKeys()
    {
        List<(AiProvider Provider, string Secret, DateTimeOffset SavedAt)> moving;
        lock (_gate)
        {
            moving = [.. AiProviders.All.Where(p => _waiting.ContainsKey(p.Id)).Select(p => (p, _waiting[p.Id].Secret, _waiting[p.Id].SavedAt))];
            _waiting.Clear();
        }
        foreach (var (provider, secret, savedAt) in moving)
        {
            if (_keys.Get(provider.Id) is null)
                _keys.Upsert(provider.Id, new AiManagementKey { Provider = provider.Id, Secret = secret, SavedAt = savedAt });
            DeleteOldFile(provider);
        }
    }

    /// <summary>The user saved or removed this key: an old one waiting no longer counts.</summary>
    private void Forget(AiProvider provider)
    {
        bool had;
        lock (_gate) had = _waiting.Remove(provider.Id);
        if (had) DeleteOldFile(provider);
    }

    private void DeleteOldFile(AiProvider provider)
    {
        try
        {
            File.Delete(OldFile(provider));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: it is read again next time and moves only where no key is synced.
        }
    }
}
