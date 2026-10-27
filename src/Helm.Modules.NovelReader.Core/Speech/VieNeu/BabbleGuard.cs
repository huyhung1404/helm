using System.Text.RegularExpressions;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// VieNeu's guards against a short sentence that does not stop (from its core_utils): a cap on frames from the length
/// of the phonemes, and a check after the sound is made that a sentence of one to three syllables has no more bursts of
/// sound than syllables ("Được." coming out as "Được không?"); such a sentence is made again.
/// </summary>
public static partial class BabbleGuard
{
    public const int MaxFrames = 300;
    public const int Retries = 2;

    private const double FramesPerPhone = 2.0;
    private const int FrameSlack = 24;
    private const int SingleWordMaxFrames = 13;
    private const int FramesPerExtraSyllable = 5;
    private const int SyllableCapMaxSyllables = 4;
    private const int SingleWordMaxPhones = 24;
    private const int CheckedMaxSyllables = 3;
    private const double BurstThresholdDb = -18.0;
    private const int BurstMinGapMs = 60;
    private const int BurstMinMs = 30;
    private const string Vowels = "aeiouyæɐɑɒɔəɘɛɜɤɯɵøœʉʊʌɪɨɚɝᵻᵿ";

    /// <summary>The most frames a sentence with these phonemes may reasonably take.</summary>
    public static int FrameCap(string phonemes)
    {
        var stripped = Markup().Replace(phonemes, "");
        var length = stripped.Length;
        var cap = FrameSlack + (int)Math.Ceiling(FramesPerPhone * length);
        var syllables = Math.Max(1, Syllables(phonemes));
        if (syllables <= SyllableCapMaxSyllables && length <= SingleWordMaxPhones * syllables)
            cap = Math.Min(cap, SingleWordMaxFrames + FramesPerExtraSyllable * (syllables - 1));
        return Math.Min(MaxFrames, cap);
    }

    /// <summary>
    /// Syllables in a sea-g2p phoneme string: one per Vietnamese word; an English word counts its vowel groups, a stress
    /// mark after a vowel starting a new one.
    /// </summary>
    public static int Syllables(string phonemes)
    {
        var total = 0;
        foreach (var token in Markup().Replace(phonemes, "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int groups = 0;
            bool inVowel = false, consonantSeen = true;
            foreach (var ch in token)
            {
                if (Vowels.Contains(ch))
                {
                    if (!inVowel && consonantSeen) groups++;
                    inVowel = true;
                    consonantSeen = false;
                }
                else if (ch is 'ː' or 'ˈ' or 'ˌ' || char.IsDigit(ch))
                {
                    if (ch is 'ˈ' or 'ˌ' && groups > 0)
                    {
                        inVowel = false;
                        consonantSeen = true;
                    }
                    else
                    {
                        inVowel = false;
                    }
                }
                else
                {
                    inVowel = false;
                    consonantSeen = true;
                }
            }
            if (token.Any(char.IsLetter)) total += Math.Max(1, groups);
        }
        return total;
    }

    /// <summary>Whether a sentence just made may have run on, and how bad it is (to keep the better of two tries).</summary>
    public static (bool Suspect, int Bursts, int Frames) Check(ReadOnlySpan<float> wav, int sampleRate, string phonemes, int cap, int frames)
    {
        var syllables = Syllables(phonemes);
        if (syllables == 0 || syllables > CheckedMaxSyllables) return (false, 0, frames);
        var bursts = SpeechBursts(wav, sampleRate);
        var hitCap = syllables <= 2 && frames >= cap - 1;
        return (bursts > syllables || hitCap, bursts, frames);
    }

    /// <summary>Whether <paramref name="next"/> is a better take than <paramref name="best"/>.</summary>
    public static bool Better((bool Suspect, int Bursts, int Frames) next, (bool Suspect, int Bursts, int Frames) best) =>
        next.Suspect != best.Suspect ? !next.Suspect : next.Bursts < best.Bursts || next.Bursts == best.Bursts && next.Frames < best.Frames;

    /// <summary>Bursts of sound (about one per syllable): 10 ms windows louder than 18 dB under the loudest.</summary>
    public static int SpeechBursts(ReadOnlySpan<float> wav, int sampleRate)
    {
        var hop = Math.Max(1, sampleRate / 100);
        var n = wav.Length / hop;
        if (n == 0) return 0;
        var envelope = new double[n];
        var peak = 0.0;
        for (var i = 0; i < n; i++)
        {
            var sum = 0.0;
            foreach (var s in wav.Slice(i * hop, hop)) sum += s * s;
            envelope[i] = Math.Sqrt(sum / hop);
            peak = Math.Max(peak, envelope[i]);
        }
        if (peak <= 1e-6) return 0;
        var threshold = peak * Math.Pow(10, BurstThresholdDb / 20);
        var minGap = Math.Max(1, BurstMinGapMs / 10);
        var minLength = Math.Max(1, BurstMinMs / 10);
        var count = 0;
        int start = -1, lastOn = -1;
        for (var i = 0; i < n; i++)
        {
            if (envelope[i] <= threshold) continue;
            if (start < 0)
            {
                start = i;
            }
            else if (i - lastOn > minGap)
            {
                if (lastOn - start + 1 >= minLength) count++;
                start = i;
            }
            lastOn = i;
        }
        if (start >= 0 && lastOn - start + 1 >= minLength) count++;
        return count;
    }

    [GeneratedRegex(@"<\|emotion_\d+\|>|</?en>")]
    private static partial Regex Markup();
}
