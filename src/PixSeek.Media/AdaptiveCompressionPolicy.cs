using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Versioned product heuristics, not universal just-noticeable-difference thresholds.</summary>
public sealed record QualityTargets(double ImageScore, double ImageWindowScore, double VideoMean, double VideoPercentile5, double? Butteraugli);
public static class AdaptiveCompressionPolicy
{
    public const string Version = "adaptive-v2.1-complete-export";
    private static readonly double[] Anchors = [0, 35, 65, 90, 100];
    public static double PresetQuality(CompressionPreset preset) => (int)preset switch { 0 => 90, 2 => 35, _ => 65 };
    public static QualityTargets Targets(double quality)
    {
        quality = Math.Clamp(quality, 0, 100);
        return new(Interpolate(quality, [50, 75, 85, 90, 95]), Interpolate(quality, [40, 65, 80, 85, 90]),
            Interpolate(quality, [75, 90, 95, 98, 99]), Interpolate(quality, [60, 82, 90, 95, 97]),
            quality >= 90 ? 1 - (quality - 90) * .05 : null);
    }
    public static double Interpolate(double quality, IReadOnlyList<double> values)
    {
        quality = Math.Clamp(quality, 0, 100);
        for (var i = 1; i < Anchors.Length; i++)
            if (quality <= Anchors[i]) return values[i - 1] + (values[i] - values[i - 1]) * (quality - Anchors[i - 1]) / (Anchors[i] - Anchors[i - 1]);
        return values[^1];
    }
    public static TimeSpan Budget(MediaMetadata media, MediaType type) => TimeSpan.FromSeconds(type == MediaType.Video
        ? Math.Max(5, media.DurationMilliseconds / 1000d * .3)
        : Math.Clamp(5 * (double)media.Width * media.Height / 12_000_000, 1, 10));
    public static double Seed(EncoderDescriptor encoder, double quality)
    {
        if (encoder.CodecId == "jxl") return Interpolate(quality, [5, 2.5, 1, .5, .2]);
        if (encoder.CodecId is "avif" or "heic") return Interpolate(quality, [35, 65, 80, 90, 95]);
        if (encoder.CodecId == "png") return 6;
        double[]? points = encoder.Id switch
        {
            "av1_nvenc" => [32, 25, 18], "hevc_nvenc" => [30, 24, 18], "h264_nvenc" => [27, 21, 16],
            "libsvtav1" or "libaom-av1" => [36, 28, 20], "libx265" => [28, 22, 16],
            "libx264" => [25, 20, 15], "libvpx-vp9" => [36, 28, 18],
            _ when encoder.RateControl == "icq" => [28, 23, 18],
            _ when encoder.Id.EndsWith("videotoolbox", StringComparison.Ordinal) => [50, 65, 80], _ => null
        };
        if (points == null) return (encoder.Minimum + encoder.Maximum) / 2;
        var q = quality <= 65 ? points[0] + (points[1] - points[0]) * (quality - 35) / 30
            : points[1] + (points[2] - points[1]) * (quality - 65) / 25;
        return Snap(encoder, q);
    }
    public static double Snap(EncoderDescriptor encoder, double quality) => Math.Clamp(
        Math.Round(quality / Math.Max(.001, encoder.Step)) * Math.Max(.001, encoder.Step), encoder.Minimum, encoder.Maximum);
    public static double Improve(EncoderDescriptor e, double q) => e.CodecId switch
    {
        "jxl" => Math.Max(0, q * .65), "avif" or "heic" => Math.Min(100, q + 8),
        _ => Snap(e, q + (e.LowerIsBetter ? -4 : 4))
    };
    public static double CompressMore(EncoderDescriptor e, double q) => e.CodecId switch
    {
        "jxl" => Math.Min(25, q * 1.3), "avif" or "heic" => Math.Max(0, q - 5),
        _ => Snap(e, q + (e.LowerIsBetter ? 4 : -4))
    };
    public static IReadOnlyList<SampleWindow> SampleWindows(double duration, bool final = false, bool perceptual = false)
    {
        if (duration <= 0) return [];
        if (duration <= 6) return [new(0, duration)];
        double[] positions = final ? perceptual ? [.05, .1, .2, .3, .4, .5, .6, .7, .8, .9, .95, .98]
            : [.1, .25, .5, .65, .8, .9] : [.1, .5, .9];
        var merged = new List<SampleWindow>();
        foreach (var w in positions.Select(p => new SampleWindow(Math.Clamp(duration * p - 1, 0, duration - 2), 2)).OrderBy(w => w.StartSeconds))
        {
            if (merged.Count > 0 && w.StartSeconds <= merged[^1].StartSeconds + merged[^1].DurationSeconds)
            {
                var previous = merged[^1];
                merged[^1] = previous with { DurationSeconds = Math.Max(previous.StartSeconds + previous.DurationSeconds, w.StartSeconds + w.DurationSeconds) - previous.StartSeconds };
            }
            else merged.Add(w);
        }
        return merged;
    }
    public static IReadOnlyList<(int Width, int Height)> SmallerVideoSizes(int width, int height)
    {
        var portrait = height > width;
        var longEdge = Math.Max(width, height);
        var shortEdge = Math.Min(width, height);
        return new[] { 2160, 1440, 1080, 720 }.Where(e => e < shortEdge).Take(2).Select(e =>
        {
            var scaledLong = Math.Max(2, (int)Math.Round(longEdge * (double)e / shortEdge / 2) * 2);
            return portrait ? (e, scaledLong) : (scaledLong, e);
        }).ToArray();
    }
}
public sealed record SampleWindow(double StartSeconds, double DurationSeconds);
public sealed record AudioEncodingPlan(int InputIndex, int OutputIndex, string Codec, int? TargetBitRate, int Channels, string ChannelLayout, bool Copy, string Reason);
public sealed record PreparedCompressionPlan
{
    public CompressionPlan Command { get; init; } = new("", "", [], false, false);
    public string CodecId => Command.CodecId;
    public string EncoderId => Command.EncoderId;
    public int Width => Command.Width;
    public int Height => Command.Height;
    public double Quality => Command.NativeQuality;
    public bool IsVideoCopy => Command.IsVideoCopy;
    public bool IsAutomatic { get; init; }
    public MediaMetadata SourceMetadata { get; init; } = new(0, 0, 0, "");
    public PerceptualQualityReport? QualityReport { get; init; }
    public IReadOnlyList<AudioEncodingPlan> AudioPlans => Command.AudioPlans;
    public string? CandidatePath { get; init; }
    public string? ReferencePath { get; init; }
    public string Reason { get; init; } = "";
    public double EstimatedSeconds { get; init; }
    public string? SkipReason => Command.SkipReason;
}
public sealed record CompressionCandidate(PreparedCompressionPlan Plan, long EstimatedBytes, double EstimatedSeconds);
public static class CompressionCandidateSelection
{
    public static CompressionCandidate? Select(IReadOnlyList<CompressionCandidate> candidates, double budgetSeconds)
    {
        var valid = candidates.Where(c => c.Plan.SkipReason == null && (!c.Plan.IsAutomatic || c.Plan.QualityReport?.Passed == true)).ToArray();
        if (valid.Length == 0) return null;
        var fast = valid.Where(c => c.EstimatedSeconds <= budgetSeconds).ToArray();
        if (fast.Length == 0) return valid.OrderBy(c => c.EstimatedSeconds).ThenBy(c => c.EstimatedBytes).First();
        var smallest = fast.Min(c => c.EstimatedBytes);
        return fast.Where(c => c.EstimatedBytes <= smallest * 1.02).OrderBy(c => c.EstimatedSeconds).ThenBy(c => c.EstimatedBytes).First();
    }
}
