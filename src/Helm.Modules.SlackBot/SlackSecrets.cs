using System.Text.RegularExpressions;

namespace Helm.Modules.SlackBot;

/// <summary>
/// The last check before Lumi posts something it wrote after reading a project: anything that looks like a key, a token,
/// a password or a private key stops the answer (Claude was told never to quote them; this is in case it did).
/// </summary>
public static partial class SlackSecrets
{
    public static bool LooksSecret(string text) => Patterns().IsMatch(text);

    [GeneratedRegex(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----|\bAKIA[0-9A-Z]{16}\b|\bxox[abposr]-[0-9A-Za-z-]{10,}|\bxapp-[0-9A-Za-z-]{10,}|\bgh[pousr]_[0-9A-Za-z]{30,}|\bgithub_pat_[0-9A-Za-z_]{30,}|\bAIza[0-9A-Za-z_-]{35}\b|\bsk-[0-9A-Za-z_-]{20,}|\b\d{6,}:AA[0-9A-Za-z_-]{30,}|(?i:\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|connection[_-]?string)\b\s*[:=]\s*[""']?[^\s""']{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex Patterns();
}
