using PixSeek.Core;
using Xunit;

namespace PixSeek.Tests;
public sealed class CompressionTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Theory]
    [InlineData(".png", false)]
    [InlineData(".jpg", true)]
    public async Task JpegXl_LosslessIsActuallyVerified_AndSourceSurvives(string ext, bool reversible)
    {
        var file = await e.ImageAsync("préservation " + Guid.NewGuid() + ext);
        var before = await File.ReadAllBytesAsync(file);
        var meta = await e.Images.IdentifyAsync(file);
        var request = new EncodingRequest(file, file + ".jxl", MediaType.Image, meta, new() { Preset = CompressionPreset.Preserve, KeepOnlySmaller = false });
        var result = await e.Encoder.EncodeAsync(request, new Progress<EncodingProgress>(), default);
        Assert.Equal("Converti", result.Status);
        Assert.True(File.Exists(result.DestinationPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        if (reversible)
            Assert.Contains("octet", result.Verification);
        else
            Assert.Contains("Pixels décodés identiques", result.Verification);
    }

    [Fact]
    public async Task Video_Av1RetainsAudioAndOriginal()
    {
        var file = Path.Combine(e.Work, "vidéo avec son é.mp4");
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2", "-c:v", "libx264", "-crf", "16", "-c:a", "aac", file]);
        var original = await File.ReadAllBytesAsync(file);
        var meta = await e.Probe.ProbeAsync(file);
        var result = await e.Encoder.EncodeAsync(new(file, file + ".mkv", MediaType.Video, meta, new() { KeepOnlySmaller = false, Speed = 12 }), new Progress<EncodingProgress>(), default);
        Assert.True(result.Status == "Converti", result.Error);
        var output = await e.Probe.ProbeAsync(result.DestinationPath!);
        Assert.Equal("av1", output.Codec);
        Assert.Equal(meta.AudioStreams, output.AudioStreams);
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task PreserveMode_DoesNotLossilyEncodeVideo()
    {
        var file = await e.ImageAsync();
        var result = await e.Encoder.EncodeAsync(new(file, file + ".mkv", MediaType.Video, new(320, 240, 1000, "h264"), new() { Preset = CompressionPreset.Preserve }), new Progress<EncodingProgress>(), default);
        Assert.Equal("Ignoré", result.Status);
        Assert.Null(result.DestinationPath);
    }

    [Fact]
    public async Task BiggerOutput_IsDiscarded()
    {
        var file = await e.ImageAsync("tiny.png");
        var request = new EncodingRequest(file, file + ".jxl", MediaType.Image, await e.Images.IdentifyAsync(file), new() { Preset = CompressionPreset.Preserve });
        var result = await e.Encoder.EncodeAsync(request, new Progress<EncodingProgress>(), default);
        Assert.True(result.Status is "Converti" or "Aucun gain", result.Error);
        if (result.Status == "Aucun gain")
            Assert.False(File.Exists(request.DestinationPath));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Cancellation_KillsProcessTree()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Runner.RunAsync(e.Tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=128x128:rate=1", "-f", "null", "-"], cts.Token));
    }

    [Fact]
    public async Task InvalidInput_IsReported_AndNeverDeleted()
    {
        var file = Path.Combine(e.Work, "corrompu.jpg");
        await File.WriteAllTextAsync(file, "not an image");
        var result = await e.Encoder.EncodeAsync(new(file, file + ".jxl", MediaType.Image, new(10, 10, 0, "JPEG"), new()), new Progress<EncodingProgress>(), default);
        Assert.Equal("Échec", result.Status);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".jxl"));
    }

    [Fact]
    public async Task Avif_EncodesAndPreservesAlpha_AndLargerOutputsAreDiscarded()
    {
        var file = Path.Combine(e.Work, "alpha.png");
        await e.Images.RunAsync(["-size", "256x256", "xc:none", "-fill", "#FF3300AA", "-draw", "circle 128,128 100,20", "-strip", file]);
        var meta = await e.Images.IdentifyAsync(file);
        Assert.True(meta.HasAlpha);
        var r = await e.Encoder.EncodeAsync(new(file, file + ".avif", MediaType.Image, meta, new() { ImageCodec = "AVIF", KeepOnlySmaller = false, KeepMetadata = false }), new Progress<EncodingProgress>(), default);
        Assert.True(r.Status == "Converti", r.Error);
        Assert.True((await e.Images.IdentifyAsync(r.DestinationPath!)).HasAlpha);
        var tiny = Path.Combine(e.Work, "onepixel.png");
        await e.Images.RunAsync(["-size", "1x1", "xc:red", "-strip", tiny]);
        var bigger = await e.Encoder.EncodeAsync(new(tiny, tiny + ".avif", MediaType.Image, await e.Images.IdentifyAsync(tiny), new() { ImageCodec = "AVIF", KeepMetadata = false }), new Progress<EncodingProgress>(), default);
        Assert.Equal("Aucun gain", bigger.Status);
        Assert.True(bigger.OutputBytes > bigger.OriginalBytes);
        Assert.Equal(0, bigger.SavedBytes);
        Assert.False(File.Exists(tiny + ".avif"));
    }

    [Fact]
    public async Task JpegXl_HighQualityAndLosslessAlphaIcc_AreDecodable()
    {
        var file = Path.Combine(e.Work, "alpha avec profil.png");
        await e.Images.RunAsync(["-size", "128x128", "xc:none", "-fill", "#33AAFF99", "-draw", "rectangle 20,20 100,100", "-profile", Path.Combine(e.Tools.Capabilities!.NativeDirectory, "sRGB.icc"), "-define", "png:include-chunk=iCCP", "-define", "png:exclude-chunk=sRGB", file]);
        Assert.NotEmpty((await e.Images.CriticalMetadataAsync(file, default))["iccHash"]);
        var meta = await e.Images.IdentifyAsync(file);
        foreach (var preset in new[]
        {
            CompressionPreset.Preserve,
            CompressionPreset.HighQuality
        }

        )
        {
            var output = file + preset + ".jxl";
            var r = await e.Encoder.EncodeAsync(new(file, output, MediaType.Image, meta, new() { Preset = preset, KeepOnlySmaller = false }), new Progress<EncodingProgress>(), default);
            Assert.True(r.Status == "Converti", r.Error);
            var decoded = await e.Images.IdentifyAsync(output);
            Assert.True(decoded.HasAlpha);
        }
    }
}
