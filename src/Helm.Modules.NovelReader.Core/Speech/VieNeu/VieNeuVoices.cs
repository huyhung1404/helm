using System.Text.Json;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// A preset voice of VieNeu v3 Turbo: who speaks (a 192-value speaker embedding) and how (a few seconds of their speech
/// as codec frames, 16 codes each).
/// </summary>
public sealed record VieNeuVoicePreset(string Id, string Name, string Description, bool Featured, float[] SpeakerEmbedding, long[][] ReferenceCodes);

/// <summary>
/// The women's voices VieNeu v3 Turbo ships (Resources/vieneu-voices.json, from its voices_v3_turbo.json, Apache-2.0):
/// the only ones Novel Reader offers. Small enough to ship with Helm; the model itself is downloaded.
/// </summary>
public static class VieNeuVoices
{
    /// <summary>The voice reading starts with: natural, Northern.</summary>
    public const string DefaultId = "ngoc_huyen";

    /// <summary>The encoder's padding frame: a short click the model learned at the end of a reference, so it is cut.</summary>
    private const long EncoderPadCode = 455;

    private static readonly Lazy<IReadOnlyList<VieNeuVoicePreset>> Loaded = new(Load);

    /// <summary>The editors' picks first (in their order), then the rest by name.</summary>
    public static IReadOnlyList<VieNeuVoicePreset> All => Loaded.Value;

    public static VieNeuVoicePreset? Find(string id) => All.FirstOrDefault(v => v.Id == id);

    private static IReadOnlyList<VieNeuVoicePreset> Load()
    {
        using var stream = typeof(VieNeuVoices).Assembly.GetManifestResourceStream("Helm.Modules.NovelReader.vieneu-voices.json")
            ?? throw new InvalidOperationException("The VieNeu voices are missing from the build.");
        using var document = JsonDocument.Parse(stream);
        var voices = new List<(int Featured, VieNeuVoicePreset Voice)>();
        foreach (var v in document.RootElement.GetProperty("voices").EnumerateArray())
        {
            var codes = v.GetProperty("codes").EnumerateArray()
                .Select(frame => frame.EnumerateArray().Select(c => c.GetInt64()).ToArray())
                .ToList();
            if (codes.Count > 1 && codes[^1][0] == EncoderPadCode) codes.RemoveAt(codes.Count - 1);
            var featured = v.GetProperty("featured").GetInt32();
            voices.Add((featured, new VieNeuVoicePreset(
                v.GetProperty("id").GetString()!,
                v.GetProperty("name").GetString()!,
                v.GetProperty("description").GetString()!,
                featured > 0,
                v.GetProperty("speaker_emb").EnumerateArray().Select(x => x.GetSingle()).ToArray(),
                codes.ToArray())));
        }
        return voices
            .OrderBy(v => v.Featured > 0 ? v.Featured : int.MaxValue)
            .ThenBy(v => v.Voice.Name, StringComparer.Ordinal)
            .Select(v => v.Voice)
            .ToList();
    }
}
