using System.Security.Cryptography;
using System.Text;

namespace Helm.Core.Sync;

/// <summary>DPAPI for the current Windows user; the purpose is the entropy (same files as Helm 0.5–0.7).</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data, string purpose) =>
        ProtectedData.Protect(data, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data, string purpose) =>
        ProtectedData.Unprotect(data, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
}
