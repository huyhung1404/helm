using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;

namespace Helm.Core.Sync;

/// <summary>The server and the access token this device uses.</summary>
public sealed record SyncCredentials(Uri Server, string Token);

public interface ISyncCredentialStore
{
    SyncCredentials? Load();

    void Save(SyncCredentials credentials);

    void Clear();

    /// <summary>Raised after <see cref="Save"/> or <see cref="Clear"/>.</summary>
    event EventHandler? Changed;
}

/// <summary>Keeps the token in a DPAPI-protected file for the current Windows user, like the master key.</summary>
public sealed class DpapiSyncCredentialStore(string filePath) : ISyncCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Helm.Sync.Credentials.v1");
    private readonly object _gate = new();
    private SyncCredentials? _cached;
    private bool _loaded;

    public string FilePath { get; } = filePath;

    public event EventHandler? Changed;

    public SyncCredentials? Load()
    {
        lock (_gate)
        {
            if (_loaded) return _cached;
            _cached = Read();
            _loaded = true;
            return _cached;
        }
    }

    public void Save(SyncCredentials credentials)
    {
        if (!SyncToken.TryParse(credentials.Token, out _)) throw new ArgumentException("Not a valid Helm sync token.", nameof(credentials));
        var json = JsonSerializer.SerializeToUtf8Bytes(new Stored(credentials.Server.ToString(), credentials.Token), HelmJson.Options);
        var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(json);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, FilePath, overwrite: true);
        lock (_gate)
        {
            _cached = credentials;
            _loaded = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        lock (_gate)
        {
            _cached = null;
            _loaded = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SyncCredentials? Read()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<Stored>(json, HelmJson.Options);
            CryptographicOperations.ZeroMemory(json);
            if (stored is null || !Uri.TryCreate(stored.Server, UriKind.Absolute, out var server)) return null;
            return SyncToken.TryParse(stored.Token, out _) ? new SyncCredentials(server, stored.Token) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record Stored(string Server, string Token);
}

/// <summary>Tests: credentials in memory only.</summary>
public sealed class InMemorySyncCredentialStore(SyncCredentials? credentials = null) : ISyncCredentialStore
{
    private SyncCredentials? _credentials = credentials;

    public event EventHandler? Changed;

    public SyncCredentials? Load() => _credentials;

    public void Save(SyncCredentials credentials)
    {
        _credentials = credentials;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _credentials = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
