using System.Security.Cryptography;
using System.Text.Json;

namespace Helm.Core.Sync;

/// <summary>
/// The account's master keys by epoch. The epoch goes up each time the key is rotated (a device was removed); new
/// payloads are sealed with <see cref="Current"/>, older ones stay readable with the key of their epoch.
/// </summary>
public sealed class SyncKeySet : IDisposable
{
    private readonly SortedDictionary<int, byte[]> _keys;

    public SyncKeySet(int currentEpoch, IReadOnlyDictionary<int, byte[]> keys)
    {
        if (!keys.ContainsKey(currentEpoch)) throw new ArgumentException("The current epoch has no key.", nameof(keys));
        foreach (var (epoch, key) in keys)
        {
            if (epoch < 1) throw new ArgumentOutOfRangeException(nameof(keys), "Epochs start at 1.");
            if (key.Length != SyncKeyring.KeySize) throw new ArgumentException($"Every key must be {SyncKeyring.KeySize} bytes.", nameof(keys));
        }
        CurrentEpoch = currentEpoch;
        _keys = new SortedDictionary<int, byte[]>(keys.ToDictionary(k => k.Key, k => k.Value.ToArray()));
    }

    public static SyncKeySet Single(ReadOnlySpan<byte> master, int epoch = 1) =>
        new(epoch, new Dictionary<int, byte[]> { [epoch] = master.ToArray() });

    public int CurrentEpoch { get; }

    public byte[] Current => _keys[CurrentEpoch];

    public IReadOnlyDictionary<int, byte[]> Keys => _keys;

    public bool TryGet(int epoch, out byte[] key) => _keys.TryGetValue(epoch, out key!);

    /// <summary>{"e": currentEpoch, "k": {"1": base64, ...}}; stored by <see cref="IMasterKeyStore"/> (DPAPI).</summary>
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(new Stored(CurrentEpoch,
        _keys.ToDictionary(k => k.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), k => Convert.ToBase64String(k.Value))));

    /// <summary>Also accepts the bare 32-byte key written by Helm 0.5.0 (epoch 1).</summary>
    public static SyncKeySet? TryParse(byte[]? data)
    {
        if (data is null) return null;
        if (data.Length == SyncKeyring.KeySize) return Single(data);
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(data);
            if (stored?.K is null) return null;
            return new SyncKeySet(stored.E, stored.K.ToDictionary(
                k => int.Parse(k.Key, System.Globalization.CultureInfo.InvariantCulture), k => Convert.FromBase64String(k.Value)));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
    }

    private sealed record Stored(int E, Dictionary<string, string> K);
}
