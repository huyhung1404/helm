using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;

namespace Helm.Modules.Vault.Autofill;

/// <summary>
/// Who signed an app: the SHA-256 of its signing certificate, which another app cannot copy even when it takes the
/// same package name. Android 11+ needs the app to be visible (the launcher query in AndroidManifest.xml).
/// </summary>
internal static class AppSigning
{
    /// <returns>Lower-case hex ("+" between several signers, sorted); null when the app or its signature cannot be read.</returns>
    public static string? CertOf(Context context, string package)
    {
        if (string.IsNullOrWhiteSpace(package) || context.PackageManager is not { } manager) return null;
        try
        {
            Signature[]? signatures;
#pragma warning disable CA1422, CS0618 // The int-flags overload is the one every supported version (8 and up) has.
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                signatures = manager.GetPackageInfo(package, PackageInfoFlags.SigningCertificates)?.SigningInfo?.GetApkContentsSigners();
            else
                signatures = manager.GetPackageInfo(package, PackageInfoFlags.Signatures)?.Signatures?.ToArray();
#pragma warning restore CA1422, CS0618
            if (signatures is not { Length: > 0 }) return null;
            return string.Join('+', signatures
                .Select(s => s.ToByteArray())
                .OfType<byte[]>()
                .Select(bytes => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
                .Order(StringComparer.Ordinal));
        }
        catch (Java.Lang.Exception)
        {
            return null; // Not installed, or not visible to Helm: no certificate, so nothing is offered in one tap.
        }
    }
}
