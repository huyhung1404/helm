namespace Helm.Tests;

internal static class TestKeys
{
    /// <summary>A fixed device-local key, so a replica reopened in the same test decrypts.</summary>
    public static byte[] Local { get; } = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
}
