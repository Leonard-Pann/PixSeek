using System.Text.Json;
using PixSeek.Core;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;

public sealed class AdaptiveCompressionPolicyTests
{
    [Theory]
    [InlineData(CompressionPreset.PerceptualLossless, 90, 90, 85, 98, 95, 1d)]
    [InlineData(CompressionPreset.Quality, 65, 85, 80, 95, 90, null)]
    [InlineData(CompressionPreset.BestCompression, 35, 75, 65, 90, 82, null)]
    public void Presets_HaveExplicitPerceptualTargets(CompressionPreset preset, double quality, double image,
        double imageWindow, double videoMean, double videoP5, double? butteraugli)
    {
        Assert.Equal(quality, AdaptiveCompressionPolicy.PresetQuality(preset));
        Assert.Equal(new QualityTargets(image, imageWindow, videoMean, videoP5, butteraugli), AdaptiveCompressionPolicy.Targets(quality));
    }

    [Theory]
    [InlineData(-100, 50, 40, 75, 60, null)]
    [InlineData(0, 50, 40, 75, 60, null)]
    [InlineData(50, 80, 72.5, 92.5, 86, null)]
    [InlineData(77.5, 87.5, 82.5, 96.5, 92.5, null)]
    [InlineData(95, 92.5, 87.5, 98.5, 96, .75)]
    [InlineData(100, 95, 90, 99, 97, .5)]
    [InlineData(150, 95, 90, 99, 97, .5)]
    public void AutoSlider_InterpolatesTargets_AndClampsEndpoints(double quality, double image, double imageWindow,
        double videoMean, double videoP5, double? butteraugli) =>
        Assert.Equal(new QualityTargets(image, imageWindow, videoMean, videoP5, butteraugli), AdaptiveCompressionPolicy.Targets(quality));

    [Fact]
    public void IncreasingAutoQuality_NeverRelaxesAnyFidelityThreshold()
    {
        var previous = AdaptiveCompressionPolicy.Targets(0);
        for (var quality = 1; quality <= 100; quality++)
        {
            var current = AdaptiveCompressionPolicy.Targets(quality);
            Assert.True(current.ImageScore >= previous.ImageScore);
            Assert.True(current.ImageWindowScore >= previous.ImageWindowScore);
            Assert.True(current.VideoMean >= previous.VideoMean);
            Assert.True(current.VideoPercentile5 >= previous.VideoPercentile5);
            if (current.Butteraugli != null && previous.Butteraugli != null) Assert.True(current.Butteraugli <= previous.Butteraugli);
            previous = current;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sampling_RejectsUnknownOrNonPositiveDurations(double seconds) =>
        Assert.Empty(AdaptiveCompressionPolicy.SampleWindows(seconds, final: true, perceptual: true));

    [Theory]
    [InlineData(.04)]
    [InlineData(1)]
    [InlineData(6)]
    public void ShortVideos_AreMeasuredOnceAcrossTheirWholeDuration(double seconds)
    {
        foreach (var final in new[] { false, true })
            foreach (var perceptual in new[] { false, true })
                Assert.Equal(new SampleWindow(0, seconds), Assert.Single(AdaptiveCompressionPolicy.SampleWindows(seconds, final, perceptual)));
    }

    [Fact]
    public void DenseFinalSamples_MergeOverlappingWindows_AndStayInsideSource()
    {
        Assert.Equal(new SampleWindow(0, 8), Assert.Single(AdaptiveCompressionPolicy.SampleWindows(8, final: true, perceptual: true)));
        foreach (var duration in new[] { 6.01, 7, 10, 30, 120 })
        {
            foreach (var final in new[] { false, true })
                foreach (var perceptual in new[] { false, true })
                {
                    var windows = AdaptiveCompressionPolicy.SampleWindows(duration, final, perceptual);
                    Assert.NotEmpty(windows);
                    foreach (var window in windows)
                    {
                        Assert.True(window.DurationSeconds > 0);
                        Assert.InRange(window.StartSeconds, 0, duration);
                        Assert.True(window.StartSeconds + window.DurationSeconds <= duration + .00001);
                    }
                    for (var i = 1; i < windows.Count; i++)
                        Assert.True(windows[i].StartSeconds > windows[i - 1].StartSeconds + windows[i - 1].DurationSeconds);
                }
        }
        Assert.Equal(3, AdaptiveCompressionPolicy.SampleWindows(120).Count);
        Assert.Equal(12, AdaptiveCompressionPolicy.SampleWindows(120, final: true, perceptual: true).Count);
    }

    [Fact]
    public void DownscaleGrid_RespectsPortraitAndLandscape_AndNeverUpscales()
    {
        Assert.Equal(new[] { (2560, 1440), (1920, 1080) }, AdaptiveCompressionPolicy.SmallerVideoSizes(3840, 2160));
        Assert.Equal(new[] { (1440, 2560), (1080, 1920) }, AdaptiveCompressionPolicy.SmallerVideoSizes(2160, 3840));
        Assert.Equal(new[] { (1280, 720) }, AdaptiveCompressionPolicy.SmallerVideoSizes(1920, 1080));
        Assert.Empty(AdaptiveCompressionPolicy.SmallerVideoSizes(1280, 720));
        foreach (var (width, height) in new[] { (4033, 3025), (3025, 4033), (3000, 2000) })
            foreach (var size in AdaptiveCompressionPolicy.SmallerVideoSizes(width, height))
            {
                Assert.True(size.Width < width && size.Height < height);
                Assert.Equal(0, size.Width % 2);
                Assert.Equal(0, size.Height % 2);
                Assert.InRange(Math.Abs(size.Width - size.Height * width / (double)height), 0, 2);
            }
    }

    [Theory]
    [InlineData(1920, 1080, "1:1", 1920, 1080)]
    [InlineData(1080, 1920, "1:1", 1080, 1920)]
    [InlineData(3840, 2160, "1:1", 3840, 2160)]
    [InlineData(720, 576, "16:15", 1440, 1080)]
    public void EvaluationCanvas_PreservesDisplayAspectRatio(int width, int height, string sar, int expectedWidth, int expectedHeight)
    {
        var media = new MediaMetadata(width, height, 10_000, "h264") { Technical = new() { SampleAspectRatio = sar } };
        Assert.Equal((expectedWidth, expectedHeight), PerceptualQualityEvaluator.EvaluationSize(media));
    }

    [Fact]
    public void CandidateSelection_PrefersFeasibleSize_ThenSpeedWithinTwoPercent()
    {
        var smallest = Candidate("smallest", 1000, 8);
        var nearTie = Candidate("near-tie", 1020, 3);
        var tooLarge = Candidate("too-large", 1021, 1);
        var overBudget = Candidate("over-budget", 500, 30);
        Assert.Same(nearTie, CompressionCandidateSelection.Select([overBudget, tooLarge, smallest, nearTie], 10));
        Assert.Same(smallest, CompressionCandidateSelection.Select([tooLarge, smallest], 10));
        Assert.Same(smallest, CompressionCandidateSelection.Select([Candidate("same-time", 1010, 8), smallest], 10));
    }

    [Fact]
    public void CandidateSelection_SoftBudgetUsesFastestValidCandidate_AndRejectsFailedQuality()
    {
        var failed = Candidate("failed", 1, 1, passed: false);
        var skipped = Candidate("skipped", 1, 1, skipped: true);
        var fast = Candidate("fast", 2000, 11);
        var slow = Candidate("slow-small", 1000, 20);
        Assert.Same(fast, CompressionCandidateSelection.Select([failed, skipped, slow, fast], 10));
        Assert.Null(CompressionCandidateSelection.Select([failed, skipped], 10));
        Assert.Null(CompressionCandidateSelection.Select([], 10));
    }

    [Fact]
    public void AutomaticCandidateWithoutMeasurement_IsRejected_WhileManualCandidateDoesNotRequireMetric()
    {
        var unmeasured = Candidate("unmeasured", 1, 1) with { Plan = new() { IsAutomatic = true } };
        var measured = Candidate("measured", 1000, 2);
        Assert.Same(measured, CompressionCandidateSelection.Select([unmeasured, measured], 10));
        var manual = Candidate("manual", 1000, 2) with { Plan = new() { IsAutomatic = false } };
        Assert.Same(manual, CompressionCandidateSelection.Select([manual], 10));
    }

    [Theory]
    [InlineData("libx264", "h264", "crf", "-crf", 18)]
    [InlineData("libx265", "hevc", "crf", "-crf", 22)]
    [InlineData("libsvtav1", "av1", "crf", "-crf", 28)]
    [InlineData("libvpx-vp9", "vp9", "crf", "-crf", 31)]
    [InlineData("av1_nvenc", "av1", "cq", "-cq", 25)]
    [InlineData("hevc_qsv", "hevc", "icq", "-global_quality", 23)]
    [InlineData("h264_amf", "h264", "cqp", "-qp_p", 24)]
    [InlineData("hevc_vaapi", "hevc", "cqp", "-qp", 24)]
    [InlineData("av1_vaapi", "av1", "cqp", "-global_quality", 112)]
    [InlineData("hevc_videotoolbox", "hevc", "quality", "-q:v", 65)]
    public void NativeBackendControls_AreNotInterpretedAsOneUniversalCrf(string id, string codec, string rateControl, string argument, double quality)
    {
        var encoder = new EncoderDescriptor { Id = id, CodecId = codec, RateControl = rateControl, Maximum = 255 };
        foreach (var args in new[] { CompressionProfileResolver.VideoQualityArguments(encoder, quality), EncoderCapabilityService.QualityArguments(encoder, quality) })
        {
            Assert.Equal(quality.ToString(System.Globalization.CultureInfo.InvariantCulture), ArgumentValue(args, argument));
            if (argument != "-crf") Assert.DoesNotContain("-crf", args);
            if (id.EndsWith("_videotoolbox", StringComparison.Ordinal)) Assert.Equal("0", ArgumentValue(args, "-allow_sw"));
        }
    }

    [Fact]
    public void PixelFormatSelection_PreservesDepthAndChroma_OrRefusesTheBackend()
    {
        var source = new MediaMetadata(1920, 1080, 10_000, "h264")
        { Technical = new() { PixelFormat = "yuv444p10le", BitDepth = 10, ChromaSubsampling = "444" } };
        var exact = new EncoderDescriptor { Id = "test", PixelFormats = ["yuv420p", "yuv444p10le"] };
        Assert.Equal("yuv444p10le", CompressionProfileResolver.SelectPixelFormat(exact, source));
        Assert.Throws<NotSupportedException>(() => CompressionProfileResolver.SelectPixelFormat(exact with { PixelFormats = ["yuv420p10le"] }, source));
        var tenBit420 = source with { Technical = source.Technical with { PixelFormat = "yuv420p10le", ChromaSubsampling = "420" } };
        Assert.Equal("p010le", CompressionProfileResolver.SelectPixelFormat(exact with { PixelFormats = ["nv12", "p010le"] }, tenBit420));
    }

    [Theory]
    [InlineData(12, "yuv444p12le", "444", "yuv444p10le")]
    [InlineData(16, "yuv420p16le", "420", "p010le")]
    public void PixelFormatSelection_NeverSilentlyReducesTwelveOrSixteenBitSources(int depth, string input, string chroma, string supported)
    {
        var source = new MediaMetadata(1920, 1080, 10_000, "hevc")
        { Technical = new() { PixelFormat = input, BitDepth = depth, ChromaSubsampling = chroma } };
        var encoder = new EncoderDescriptor { Id = "insufficient-depth", PixelFormats = [supported] };
        Assert.Throws<NotSupportedException>(() => CompressionProfileResolver.SelectPixelFormat(encoder, source));
    }

    [Fact]
    public void VmafParsing_UsesPerFrameValues_AndNearestRankLowerTail()
    {
        var frames = Enumerable.Range(1, 100).Reverse().Select(score => new { metrics = new { vmaf = (double)score } });
        var json = JsonSerializer.Serialize(new { frames, pooled_metrics = new { vmaf = new { mean = 999d } } });
        var report = PerceptualQualityEvaluator.ParseVmaf(json);
        Assert.Equal(100, report.Count);
        Assert.Equal(50.5, report.Mean);
        Assert.Equal(5, report.Percentile5);
        var shortReport = PerceptualQualityEvaluator.ParseVmaf("""
            {"frames":[{"metrics":{"vmaf_v1.0.16":100}},{"metrics":{"vmaf_v1.0.16":10}},{"metrics":{"vmaf_v1.0.16":90}},{"metrics":{"vmaf_v1.0.16":80}}]}
            """);
        Assert.Equal(70, shortReport.Mean);
        Assert.Equal(10, shortReport.Percentile5);
        Assert.Throws<InvalidDataException>(() => PerceptualQualityEvaluator.ParseVmaf("{\"frames\":[]}"));
    }

    [Fact]
    public void CombinedVideoMeasurements_CannotHideOneFailedWindowBehindTheMean()
    {
        var good = new PerceptualQualityReport { Passed = true, Score = 100, Percentile5 = 100, Windows = [new(0, 2, 100, 100)] };
        var bad = new PerceptualQualityReport { Passed = false, Score = 70, Percentile5 = 30, Windows = [new(20, 2, 70, 30)] };
        var report = PerceptualQualityEvaluator.Combine([good, bad]);
        Assert.False(report.Passed);
        Assert.Equal(85, report.Score);
        Assert.Equal(70, report.MinimumWindowScore);
        Assert.Equal(30, report.Percentile5);
        Assert.Equal(2, report.Windows.Count);
        Assert.True(report.Sampled);
        Assert.False(PerceptualQualityEvaluator.Combine([]).Passed);
    }

    private static CompressionCandidate Candidate(string id, long bytes, double seconds, bool passed = true, bool skipped = false) => new(new()
    {
        IsAutomatic = true,
        Command = skipped ? CompressionProfileResolver.Skip("not supported") : new(".mkv", "ffmpeg", [], false, false) { EncoderId = id },
        QualityReport = new() { Passed = passed, Score = passed ? 98 : 0 }
    }, bytes, seconds);

    private static string ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        var index = arguments.ToList().IndexOf(name);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
