namespace PixSeek.AI;
public static class EmbeddingMath
{
    public static float[] PreprocessRgb(ReadOnlySpan<byte> rgb, bool siglip = false)
    {
        const int pixels = 224 * 224;
        if (rgb.Length != pixels * 3) throw new ArgumentException("RGB 224 × 224 required.");
        float[] mean = siglip ? [.5f, .5f, .5f] : [.48145466f, .4578275f, .40821073f];
        float[] std = siglip ? [.5f, .5f, .5f] : [.26862954f, .26130258f, .27577711f];
        var values = new float[pixels * 3];
        for (var i = 0; i < pixels; i++)
            for (var c = 0; c < 3; c++) values[c * pixels + i] = (rgb[i * 3 + c] / 255f - mean[c]) / std[c];
        return values;
    }
    public static float[] Normalize(float[] values)
    {
        var norm = Math.Sqrt(values.Sum(x => (double)x * x));
        if (!double.IsFinite(norm) || norm < 1e-10) throw new InvalidDataException("Invalid embedding.");
        for (var i = 0; i < values.Length; i++) values[i] = (float)(values[i] / norm);
        return values;
    }
}
