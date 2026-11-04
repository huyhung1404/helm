using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Connector.Hosting;

/// <summary>
/// This box's <see cref="ISecretProtector"/>: the host key under &lt;data&gt;/secrets (<see cref="FileKeySecretProtector"/>).
/// <c>HELM_SEAL_KEY=tpm2</c> (or <c>host</c>, <c>auto</c>: systemd-creds' <c>--with-key</c>) seals a new key; a service
/// started with <c>LoadCredentialEncrypted=helm-host-key:…</c> gets it from systemd's $CREDENTIALS_DIRECTORY.
/// </summary>
internal static class ConnectorSecrets
{
    public const string SealVariable = "HELM_SEAL_KEY";

    public static string Directory(HelmPaths paths) => Path.Combine(paths.Root, "secrets");

    public static FileKeySecretProtector For(HelmPaths paths, Func<string, string?> environment)
    {
        var seal = environment(SealVariable) is { Length: > 0 } withKey ? withKey : null;
        return new FileKeySecretProtector(Directory(paths), OperatingSystem.IsLinux() ? new SystemdCredsSealer(seal ?? "tpm2") : null, seal is not null,
            environment("CREDENTIALS_DIRECTORY") is { Length: > 0 } credentials ? credentials : null);
    }
}
