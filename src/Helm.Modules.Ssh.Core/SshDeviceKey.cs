using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Helm.Core.Sync;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Helm.Modules.Ssh;

/// <summary>
/// This device's own SSH key: Ed25519, made by Helm the first time it is needed and never synced or exported. The 32
/// byte seed is kept in a file protected by <see cref="ISecretProtector"/> (DPAPI on Windows, the Android Keystore on
/// Android), so the file is useless on another device or account. Losing the device means removing one line from the
/// server's authorized_keys. Thread-safe.
/// </summary>
public sealed class SshDeviceKey
{
    private const string Purpose = "Helm.Ssh.DeviceKey.v1";
    internal const string Algorithm = "ssh-ed25519";
    private const int SeedLength = 32;

    private readonly ISecretProtector _protector;
    private readonly string _comment;
    private readonly object _gate = new();
    private byte[]? _seed;
    private byte[]? _publicKey;
    private bool _loaded;

    public SshDeviceKey(string filePath, ISecretProtector protector, string deviceName)
    {
        FilePath = filePath;
        _protector = protector;
        _comment = Comment(deviceName);
    }

    public string FilePath { get; }

    /// <summary>Raised after a new key is made.</summary>
    public event EventHandler? Changed;

    /// <summary>True once a key exists (it is made on first use, see <see cref="EnsureCreated"/>).</summary>
    public bool Exists
    {
        get
        {
            lock (_gate)
            {
                Load();
                return _seed is not null;
            }
        }
    }

    /// <summary>The line to add to ~/.ssh/authorized_keys: "ssh-ed25519 AAAA… helm@device"; empty without a key.</summary>
    public string PublicKeyLine
    {
        get
        {
            lock (_gate)
            {
                Load();
                return _publicKey is null ? "" : $"{Algorithm} {Convert.ToBase64String(PublicKeyBlob(_publicKey))} {_comment}";
            }
        }
    }

    /// <summary>"SHA256:…", as ssh-keygen -lf prints it; empty without a key.</summary>
    public string Fingerprint
    {
        get
        {
            lock (_gate)
            {
                Load();
                return _publicKey is null ? "" : "SHA256:" + Convert.ToBase64String(SHA256.HashData(PublicKeyBlob(_publicKey))).TrimEnd('=');
            }
        }
    }

    /// <summary>Makes the key if there is none yet. A key file that cannot be read (another device) is not replaced.</summary>
    /// <exception cref="CryptographicException">The key file exists but was protected elsewhere.</exception>
    public void EnsureCreated()
    {
        lock (_gate)
        {
            Load();
            if (_seed is not null) return;
            if (File.Exists(FilePath)) throw new CryptographicException("This device's SSH key cannot be read here. Make a new key in SSH settings.");
            Write(RandomNumberGenerator.GetBytes(SeedLength));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the key. Servers that only know the old key will refuse this device until the new one is added.</summary>
    public void Regenerate()
    {
        lock (_gate) Write(RandomNumberGenerator.GetBytes(SeedLength));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The key for signing in (made first if needed).</summary>
    internal IPrivateKeySource CreateKeySource()
    {
        EnsureCreated();
        lock (_gate) return new PrivateKeyFile(new ED25519Key((byte[])_seed!.Clone()));
    }

    /// <summary>The public key in SSH wire format: string "ssh-ed25519", string key (RFC 8709).</summary>
    internal static byte[] PublicKeyBlob(byte[] publicKey)
    {
        var name = Encoding.ASCII.GetBytes(Algorithm);
        var blob = new byte[4 + name.Length + 4 + publicKey.Length];
        BinaryPrimitives.WriteInt32BigEndian(blob, name.Length);
        name.CopyTo(blob, 4);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4 + name.Length), publicKey.Length);
        publicKey.CopyTo(blob, 8 + name.Length);
        return blob;
    }

    /// <summary>"helm@&lt;device&gt;", so the line says where it came from in authorized_keys.</summary>
    internal static string Comment(string deviceName)
    {
        var device = new string((deviceName ?? "").Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
        return "helm@" + (device.Length == 0 ? "device" : device);
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(FilePath)) return;
        try
        {
            var seed = _protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
            if (seed.Length != SeedLength) return;
            Use(seed);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Left unset: EnsureCreated reports it instead of silently making a different key.
        }
    }

    private void Write(byte[] seed)
    {
        var protectedBytes = _protector.Protect(seed, Purpose);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, FilePath, overwrite: true);
        if (_seed is not null) CryptographicOperations.ZeroMemory(_seed);
        Use(seed);
        _loaded = true;
    }

    private void Use(byte[] seed)
    {
        using var key = new ED25519Key((byte[])seed.Clone());
        _publicKey = [.. key.PublicKey];
        _seed = seed;
    }
}
