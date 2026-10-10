namespace Helm.Modules.NovelReader.Speech;

/// <summary>
/// Plain 16-bit mono PCM for voices that only speak at one speed (the local VieNeu voice): changing the speed without
/// changing the pitch (WSOLA: overlap-add of windows taken where they line up best), scaling the volume, and wrapping
/// the samples in a WAV file the Windows media player can play.
/// </summary>
public static class TimeStretch
{
    /// <summary>
    /// The same speech <paramref name="rate"/> times faster (2 halves the length), at the same pitch. A rate within 1 %
    /// of 1 returns the input.
    /// </summary>
    public static short[] Stretch(short[] input, int sampleRate, double rate)
    {
        rate = Math.Clamp(rate, 0.5, 3);
        if (Math.Abs(rate - 1) < 0.01 || input.Length < sampleRate / 10) return input;
        var window = Math.Max(64, sampleRate * 30 / 1000);   // 30 ms
        var hop = window / 2;                                 // output step: 50 % overlap
        var tolerance = Math.Max(8, sampleRate * 10 / 1000);  // look ±10 ms for the best fit
        var correlate = hop / 2;
        var hann = new double[window];
        for (var i = 0; i < window; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / window);

        var outLength = (int)(input.Length / rate);
        var output = new double[outLength + window];
        var weight = new double[outLength + window];
        var previous = 0;
        for (var frame = 0; ; frame++)
        {
            var outPos = frame * hop;
            if (outPos >= outLength) break;
            var target = (int)Math.Round(frame * hop * rate);
            var start = Math.Min(target, Math.Max(0, input.Length - window));
            if (frame > 0)
            {
                // Where the previous window would naturally go on; pick the nearby start that sounds like it.
                var natural = previous + hop;
                if (natural + correlate < input.Length) start = BestStart(input, natural, target, tolerance, correlate, window);
            }
            for (var i = 0; i < window; i++)
            {
                var source = start + i;
                if (source >= input.Length) break;
                output[outPos + i] += input[source] * hann[i];
                weight[outPos + i] += hann[i];
            }
            previous = start;
        }
        var result = new short[outLength];
        for (var i = 0; i < outLength; i++)
        {
            var value = weight[i] > 1e-3 ? output[i] / weight[i] : output[i];
            result[i] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        }
        return result;
    }

    /// <summary>Multiplies every sample by <paramref name="volume"/> (0 to 1).</summary>
    public static short[] Scale(short[] samples, double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        if (volume >= 0.999) return samples;
        var result = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++) result[i] = (short)Math.Round(samples[i] * volume);
        return result;
    }

    public static short[] FromBytes(ReadOnlySpan<byte> pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
        return samples;
    }

    /// <summary>A complete WAV file (RIFF header with the real lengths) around 16-bit mono samples.</summary>
    public static byte[] Wav(short[] samples, int sampleRate)
    {
        var dataLength = samples.Length * 2;
        var wav = new byte[44 + dataLength];
        using var writer = new BinaryWriter(new MemoryStream(wav));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);        // PCM
        writer.Write((short)1);        // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);  // bytes per second
        writer.Write((short)2);        // block align
        writer.Write((short)16);       // bits per sample
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (var sample in samples) writer.Write(sample);
        return wav;
    }

    /// <summary>The start within ±tolerance of <paramref name="target"/> whose samples best match those at <paramref name="natural"/>.</summary>
    private static int BestStart(short[] input, int natural, int target, int tolerance, int length, int window)
    {
        var lowest = Math.Max(0, target - tolerance);
        var highest = Math.Min(input.Length - window, target + tolerance);
        if (highest < lowest) return Math.Max(0, Math.Min(target, input.Length - 1));
        length = Math.Min(length, input.Length - natural);
        double Score(int start)
        {
            double sum = 0;
            for (var i = 0; i < length; i += 2) sum += (double)input[start + i] * input[natural + i];
            return sum;
        }
        // Coarse search every 4 samples, then the 4 around the best one.
        var best = lowest;
        var bestScore = double.MinValue;
        for (var s = lowest; s <= highest; s += 4)
        {
            var score = Score(s);
            if (score > bestScore) (best, bestScore) = (s, score);
        }
        for (var s = Math.Max(lowest, best - 3); s <= Math.Min(highest, best + 3); s++)
        {
            var score = Score(s);
            if (score > bestScore) (best, bestScore) = (s, score);
        }
        return best;
    }
}
