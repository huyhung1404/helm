using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Helm.Modules.NovelReader.Speech;

/// <summary>What tells two recordings of a sentence apart; synthesized sound is held in memory under these keys only.</summary>
public static class SpeechKeys
{
    /// <summary>What makes two recordings of a text the same: the voice and its speed, pitch and volume.</summary>
    public static string Profile(SpeechVoice voice, SpeechOptions options) =>
        string.Create(CultureInfo.InvariantCulture, $"{voice.Id}|{options.Rate:0.##}|{options.Pitch:0.##}|{options.Volume:0.##}");

    /// <summary>A sentence's key: a hash of the profile and the text.</summary>
    public static string Key(string profile, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile + "|" + text)), 0, 16).ToLowerInvariant();

    /// <summary>
    /// Deletes the sound older versions kept on the disk (cache/novel-reader/audio): hundreds of megabytes that
    /// nothing reads any more. Best effort.
    /// </summary>
    public static void DeleteOldAudioFolder(string cacheRoot)
    {
        var folder = Path.Combine(cacheRoot, NovelReaderIds.ModuleId, "audio");
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open: what is left goes on the next start.
        }
    }
}
