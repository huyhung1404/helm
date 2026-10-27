namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// Halves a sample rate (VieNeu's 48 kHz to 24 kHz, plenty for speech and half the memory for sound made ahead): a
/// windowed-sinc low-pass at a quarter of the input rate, then every other sample, as 16-bit PCM.
/// </summary>
public static class HalfRate
{
    private const int Taps = 31;
    private static readonly float[] Kernel = BuildKernel();

    public static short[] Convert(ReadOnlySpan<float> input)
    {
        var output = new short[input.Length / 2];
        const int half = Taps / 2;
        for (var o = 0; o < output.Length; o++)
        {
            var centre = 2 * o;
            var sum = 0f;
            for (var k = 0; k < Taps; k++)
            {
                var i = centre + k - half;
                if ((uint)i < (uint)input.Length) sum += input[i] * Kernel[k];
            }
            output[o] = (short)Math.Clamp(MathF.Round(sum * short.MaxValue), short.MinValue, short.MaxValue);
        }
        return output;
    }

    private static float[] BuildKernel()
    {
        var kernel = new float[Taps];
        const int half = Taps / 2;
        var total = 0.0;
        for (var k = 0; k < Taps; k++)
        {
            var n = k - half;
            // Cut-off at a quarter of the rate (the new Nyquist), Blackman window.
            var sinc = n == 0 ? 0.5 : Math.Sin(Math.PI * n / 2) / (Math.PI * n);
            var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * k / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * k / (Taps - 1));
            kernel[k] = (float)(sinc * window);
            total += kernel[k];
        }
        for (var k = 0; k < Taps; k++) kernel[k] = (float)(kernel[k] / total);
        return kernel;
    }
}
