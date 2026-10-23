namespace PixSeek.Core;
public static class VideoSampling
{
    public static IReadOnlyList<long> Timestamps(long duration, int intervalSeconds, int maxFrames)
    {
        if (duration <= 0) return [0];
        intervalSeconds = Math.Clamp(intervalSeconds, 1, 600); maxFrames = Math.Clamp(maxFrames, 1, 1000);
        var count = Math.Min(maxFrames, Math.Max(1, (int)Math.Ceiling(duration / (intervalSeconds * 1000d))));
        if (count == 1) return [Math.Min(duration / 2, Math.Max(0, duration - 100))];
        var step = Math.Max(intervalSeconds * 1000d, (duration - 100d) / (count - 1));
        return Enumerable.Range(0, count).Select(i => (long)Math.Clamp(i * step, 0, Math.Max(0, duration - 100))).ToArray();
    }
}
