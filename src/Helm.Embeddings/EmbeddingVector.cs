namespace Helm.Embeddings;

/// <summary>
/// A sentence vector kept small for sync: each number as one signed byte (scaled so the largest is ±127), in base64,
/// 512 characters for the model's 384 numbers. Only directions are compared (cosine), so the scale is not kept.
/// </summary>
public static class EmbeddingVector
{
    public static string Encode(ReadOnlySpan<float> vector)
    {
        var max = 0f;
        foreach (var v in vector) max = Math.Max(max, Math.Abs(v));
        var bytes = new byte[vector.Length];
        if (max > 0)
            for (var i = 0; i < vector.Length; i++)
                bytes[i] = unchecked((byte)(sbyte)Math.Clamp(MathF.Round(vector[i] / max * 127), -127, 127));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The vector of <see cref="Encode"/>, its length 1; null when the text is not one.</summary>
    public static float[]? Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }
        var vector = new float[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) vector[i] = unchecked((sbyte)bytes[i]);
        return Normalize(vector) ? vector : null;
    }

    /// <summary>Scales <paramref name="vector"/> to length 1; false when it is all zeros.</summary>
    public static bool Normalize(Span<float> vector)
    {
        var norm = MathF.Sqrt(Dot(vector, vector));
        if (norm == 0 || float.IsNaN(norm)) return false;
        for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        return true;
    }

    /// <summary>The cosine of two vectors of length 1 (their dot product); 0 when their sizes differ.</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => a.Length == b.Length ? Dot(a, b) : 0;

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
