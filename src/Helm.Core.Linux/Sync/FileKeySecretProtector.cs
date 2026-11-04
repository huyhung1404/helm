using System.Security.Cryptography;
using System.Text;
using Helm.Core.Linux;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Sync;

/// <summary>
/// <see cref="ISecretProtector"/> for a Linux box with no desktop keyring: AES-256-GCM under a 32-byte host key that only
/// this user can read, the purpose bound in as associated data (so a secret protected for one purpose never opens as
/// another, like DPAPI's entropy). Where the key comes from, first match:
/// <list type="number">
/// <item>the systemd credential <see cref="CredentialName"/> (a service started with
/// <c>LoadCredentialEncrypted=helm-host-key:…/host.key.cred</c>: systemd unseals it, the disk holds only the sealed copy);</item>
/// <item>secrets/host.key.cred, sealed by <c>systemd-creds</c> (the TPM2), unsealed here;</item>
/// <item>secrets/host.key, a plain key file with mode 0600 in a 0700 folder.</item>
/// </list>
/// A new key is sealed when sealing was asked for and works on this box, else written as the plain file.
/// </summary>
public sealed class FileKeySecretProtector : ISecretProtector
{
    public const string CredentialName = "helm-host-key";
    public const string KeyFileName = "host.key";
    public const string SealedKeyFileName = "host.key.cred";

    private const int KeySize = 32, NonceSize = 12, TagSize = 16;
    private static readonly byte[] Magic = "HLK1"u8.ToArray();

    private readonly string _directory;
    private readonly IHostKeySealer? _sealer;
    private readonly bool _seal;
    private readonly string? _credentials;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private byte[]? _key;

    /// <param name="directory">The folder for the key files (the data folder's secrets/).</param>
    /// <param name="sealer">Seals a new key and unseals a sealed one; null: no sealing on this box.</param>
    /// <param name="seal">A new key is sealed (when <paramref name="sealer"/> can); else it is the plain 0600 file.</param>
    /// <param name="credentialsDirectory">systemd's $CREDENTIALS_DIRECTORY of the running service, or null.</param>
    public FileKeySecretProtector(string directory, IHostKeySealer? sealer = null, bool seal = false, string? credentialsDirectory = null,
        ILogger? logger = null)
    {
        _directory = directory;
        _sealer = sealer;
        _seal = seal;
        _credentials = credentialsDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    public string KeyFile => Path.Combine(_directory, KeyFileName);

    public string SealedKeyFile => Path.Combine(_directory, SealedKeyFileName);

    /// <summary>Where the key is (or would be) read from, for <c>helm status</c>; never reads or creates it.</summary>
    public string KeySource =>
        _credentials is { } dir && File.Exists(Path.Combine(dir, CredentialName)) ? "systemd credential (sealed at rest)"
        : File.Exists(SealedKeyFile) ? $"{SealedKeyFile} (sealed by systemd-creds)"
        : File.Exists(KeyFile) ? $"{KeyFile} (mode 0600)"
        : "none yet (made on first use)";

    public byte[] Protect(byte[] data, string purpose)
    {
        var key = Key(create: true);
        var output = new byte[Magic.Length + NonceSize + TagSize + data.Length];
        Magic.CopyTo(output, 0);
        var nonce = output.AsSpan(Magic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(Magic.Length + NonceSize + TagSize), output.AsSpan(Magic.Length + NonceSize, TagSize), Associated(purpose));
        return output;
    }

    public byte[] Unprotect(byte[] data, string purpose)
    {
        if (data.Length < Magic.Length + NonceSize + TagSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Not protected by this device's host key.");
        var key = Key(create: false);
        var plain = new byte[data.Length - Magic.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        // A wrong purpose, another box's key or a changed byte all fail the tag.
        aes.Decrypt(data.AsSpan(Magic.Length, NonceSize), data.AsSpan(Magic.Length + NonceSize + TagSize), data.AsSpan(Magic.Length + NonceSize, TagSize),
            plain, Associated(purpose));
        return plain;
    }

    private static byte[] Associated(string purpose) => Encoding.UTF8.GetBytes("Helm.FileKey.v1\n" + purpose);

    private byte[] Key(bool create)
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            _key = Load() ?? (create ? Create() : throw new CryptographicException("This device has no host key yet: nothing was protected here."));
            return _key;
        }
    }

    private byte[]? Load()
    {
        if (_credentials is { } dir && File.Exists(Path.Combine(dir, CredentialName)))
            return Checked(File.ReadAllBytes(Path.Combine(dir, CredentialName)), "the systemd credential");
        if (File.Exists(SealedKeyFile))
        {
            if (_sealer is null) throw new CryptographicException($"The host key is sealed ({SealedKeyFile}) but this helm cannot unseal it.");
            return Checked(_sealer.Unseal(File.ReadAllBytes(SealedKeyFile)), SealedKeyFile);
        }
        if (!File.Exists(KeyFile)) return null;
        PrivateFiles.Tighten(KeyFile, _logger);
        return Checked(File.ReadAllBytes(KeyFile), KeyFile);
    }

    private byte[] Create()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        PrivateFiles.CreateDirectory(_directory);
        if (_seal && _sealer is not null)
        {
            if (_sealer.Unavailable() is { } why)
                _logger.LogWarning("The host key is not sealed ({Reason}); it is a 0600 file instead", why);
            else
            {
                try
                {
                    return Kept(key, SealedKeyFile, _sealer.Seal(key));
                }
                catch (CryptographicException ex)
                {
                    _logger.LogWarning(ex, "Sealing the host key failed; it is a 0600 file instead");
                }
            }
        }
        return Kept(key, KeyFile, key);
    }

    /// <summary>
    /// Writes the new key unless another helm (the service and a command at the same moment) wrote one first: then that
    /// one is used, so both protect with the same key.
    /// </summary>
    private byte[] Kept(byte[] key, string path, byte[] contents)
    {
        if (PrivateFiles.WriteNew(path, contents)) return key;
        CryptographicOperations.ZeroMemory(key);
        return Load() ?? throw new CryptographicException("The host key file appeared but could not be read.");
    }

    private static byte[] Checked(byte[] key, string from) =>
        key.Length == KeySize ? key : throw new CryptographicException($"The host key in {from} is not {KeySize} bytes.");
}
