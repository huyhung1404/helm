namespace Helm.Core.Sync;

/// <summary>
/// Encrypts small secrets (sync token, account key, local replica key) so that only this user on this device can
/// read them back: DPAPI on Windows, an Android Keystore key on Android.
/// </summary>
public interface ISecretProtector
{
    /// <param name="purpose">Separates secrets from each other (DPAPI entropy); the same value must be used to unprotect.</param>
    byte[] Protect(byte[] data, string purpose);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The data was not protected here (other user, other device, reset key).</exception>
    byte[] Unprotect(byte[] data, string purpose);
}

/// <summary>Tests and tooling: no protection at all, only the purpose check.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data, string purpose) => [.. System.Text.Encoding.UTF8.GetBytes(purpose + "\n"), .. data];

    public byte[] Unprotect(byte[] data, string purpose)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(purpose + "\n");
        if (data.Length < prefix.Length || !data.AsSpan(0, prefix.Length).SequenceEqual(prefix))
            throw new System.Security.Cryptography.CryptographicException("Protected for another purpose.");
        return data[prefix.Length..];
    }
}
