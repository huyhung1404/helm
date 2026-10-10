namespace Helm.Modules.NovelReader.Speech;

/// <summary>
/// Cuts the silence voices leave at both ends of a sentence, so one sentence follows the next without a pause. Voices
/// pad every sentence: VieNeu with about 0.1 s before and 0.3 s after, which adds up to nearly half a second of
/// nothing between two sentences of a paragraph. Only 16-bit PCM WAV is touched (the local voices, the phone's and the
/// Windows voices); anything else (the online MP3) is returned as it is.
/// </summary>
public static class SpeechSilence
{
    /// <summary>Silence kept before the first sound: the start of a word is never clipped.</summary>
    public static readonly TimeSpan KeepBefore = TimeSpan.FromMilliseconds(15);

    /// <summary>Silence kept after the last sound: the end of a word fades out naturally.</summary>
    public static readonly TimeSpan KeepAfter = TimeSpan.FromMilliseconds(70);

    /// <summary>Quieter than this (in 16-bit samples) counts as silence, unless the whole sentence is that quiet.</summary>
    private const int Floor = 250;

    public static SpeechAudio Trim(SpeechAudio audio)
    {
        if (!TryReadWav(audio.Data, out var format, out var dataStart, out var dataLength)) return audio;
        var (channels, sampleRate) = format;
        var frame = 2 * channels;
        var frames = dataLength / frame;
        if (frames == 0) return audio;

        // The loudest sample sets what is quiet: 3 % of it, but at least the floor (a quiet voice is not all silence).
        var data = audio.Data;
        var peak = 0;
        for (var i = dataStart; i + 1 < dataStart + frames * frame; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(data, i)));
        var threshold = Math.Max(Floor, peak * 3 / 100);
        if (peak < threshold) return audio;

        bool Loud(int f)
        {
            var at = dataStart + f * frame;
            for (var c = 0; c < channels; c++)
                if (Math.Abs((int)BitConverter.ToInt16(data, at + 2 * c)) >= threshold) return true;
            return false;
        }
        var first = 0;
        while (first < frames && !Loud(first)) first++;
        var last = frames - 1;
        while (last > first && !Loud(last)) last--;
        var from = Math.Max(0, first - (int)(KeepBefore.TotalSeconds * sampleRate));
        var to = Math.Min(frames, last + 1 + (int)(KeepAfter.TotalSeconds * sampleRate));
        if (from == 0 && to == frames) return audio;
        return new SpeechAudio(Wav(data.AsSpan(dataStart + from * frame, (to - from) * frame), channels, sampleRate), audio.ContentType);
    }

    /// <summary>The format and the samples of a 16-bit PCM WAV; false for anything else.</summary>
    internal static bool TryReadWav(byte[] data, out (int Channels, int SampleRate) format, out int dataStart, out int dataLength)
    {
        format = default;
        dataStart = dataLength = 0;
        if (data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' || data[8] != 'W' || data[9] != 'A') return false;
        var pcm = false;
        for (var at = 12; at + 8 <= data.Length;)
        {
            var size = BitConverter.ToInt32(data, at + 4);
            if (size < 0) return false;
            var body = at + 8;
            if (data[at] == 'f' && data[at + 1] == 'm' && data[at + 2] == 't' && size >= 16)
            {
                pcm = BitConverter.ToInt16(data, body) == 1 && BitConverter.ToInt16(data, body + 14) == 16;
                format = (BitConverter.ToInt16(data, body + 2), BitConverter.ToInt32(data, body + 4));
            }
            else if (data[at] == 'd' && data[at + 1] == 'a' && data[at + 2] == 't' && data[at + 3] == 'a')
            {
                dataStart = body;
                // Streamed WAVs may say 0 or more than there is: the rest of the file is the samples.
                dataLength = size <= 0 || body + size > data.Length ? data.Length - body : size;
                return pcm && format.Channels is 1 or 2 && format.SampleRate > 0;
            }
            at = body + size + (size & 1);
        }
        return false;
    }

    private static byte[] Wav(ReadOnlySpan<byte> samples, int channels, int sampleRate)
    {
        var wav = new byte[44 + samples.Length];
        void Text(int at, string s) { for (var i = 0; i < 4; i++) wav[at + i] = (byte)s[i]; }
        void Int(int at, int value) => BitConverter.TryWriteBytes(wav.AsSpan(at, 4), value);
        void Short(int at, short value) => BitConverter.TryWriteBytes(wav.AsSpan(at, 2), value);
        Text(0, "RIFF");
        Int(4, 36 + samples.Length);
        Text(8, "WAVE");
        Text(12, "fmt ");
        Int(16, 16);
        Short(20, 1);
        Short(22, (short)channels);
        Int(24, sampleRate);
        Int(28, sampleRate * channels * 2);
        Short(32, (short)(channels * 2));
        Short(34, 16);
        Text(36, "data");
        Int(40, samples.Length);
        samples.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
