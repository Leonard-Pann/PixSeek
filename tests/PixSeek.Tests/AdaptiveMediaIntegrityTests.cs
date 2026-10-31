using PixSeek.Core;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;

public sealed class AdaptiveMediaIntegrityTests
{
    private const string ProbeJson = """
        {"streams":[
          {"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"pix_fmt":"yuv420p10le",
           "bits_per_raw_sample":"10","r_frame_rate":"30000/1001","avg_frame_rate":"30000/1001","time_base":"1/90000",
           "sample_aspect_ratio":"1:1","display_aspect_ratio":"16:9","duration":"12.5","nb_frames":"375",
           "color_transfer":"smpte2084","color_primaries":"bt2020","color_space":"bt2020nc","color_range":"tv",
           "side_data_list":[{"side_data_type":"Display Matrix","rotation":-90},{"side_data_type":"DOVI configuration record"}],
           "disposition":{"default":1,"attached_pic":0},"tags":{"title":"Prise A"}},
          {"index":1,"codec_type":"audio","codec_name":"aac","channels":6,"channel_layout":"5.1(side)","sample_rate":"48000",
           "bit_rate":"192000","start_time":"0.021","duration_ts":600000,"time_base":"1/48000","tags":{"language":"fra","title":"Français"},"disposition":{"default":1}},
          {"index":2,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"},"disposition":{"forced":1}},
          {"index":3,"codec_type":"video","codec_name":"mjpeg","width":100,"height":100,"disposition":{"attached_pic":1}},
          {"index":4,"codec_type":"attachment","codec_name":"ttf","extradata_hash":"SHA256:fixture-font","tags":{"filename":"Police.ttf","mimetype":"application/x-truetype-font"}}
        ],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"N/A","tags":{"title":"Vacances","creation_time":"2020-06-01T10:00:00Z"}},
        "chapters":[{"id":0,"time_base":"1/1000","start_time":"0.0","end_time":"12.5","tags":{"title":"Début"}}]}
        """;

    [Fact]
    public void Probe_DescribesPrimaryVideoAudioAndAncillaryTracks()
    {
        var m = FfprobeService.ParseJson(ProbeJson);
        Assert.Equal(3840, m.Width);
        Assert.Equal(12500, m.DurationMilliseconds);
        Assert.Equal(375, m.FrameCount);
        Assert.True(m.Hdr);
        Assert.True(m.Technical.HasDynamicHdr);
        Assert.Equal(10, m.Technical.BitDepth);
        Assert.Equal("420", m.Technical.ChromaSubsampling);
        Assert.Equal(-90, m.Technical.Rotation);
        Assert.Equal((2160, 3840), FfprobeService.DisplaySize(m));
        Assert.Equal(5, m.TotalStreams);
        Assert.Equal(1, m.AudioStreams);
        var audio = m.Technical.Streams[1];
        Assert.Equal("fra", audio.Language);
        Assert.Equal("5.1(side)", audio.ChannelLayout);
        Assert.Equal(192000L, audio.BitRate);
        Assert.Equal(12.5, audio.DurationSeconds);
        Assert.True(m.Technical.Streams[3].AttachedPicture);
        Assert.Equal("Police.ttf", m.Technical.Streams[4].Tags["filename"]);
        Assert.Contains("Début", m.Technical.Metadata["pixseek:chapters"]);
    }

    [Theory]
    [InlineData("gray", false)]
    [InlineData("graya", true)]
    [InlineData("srgb", false)]
    [InlineData("srgba", true)]
    public void Identify_DoesNotConfuseGrayWithAlpha(string channels, bool alpha)
    {
        var m = ImageMagickService.ParseIdentification($"100|200|PNG|{channels}|1|16|sRGB|RightTop\n");
        Assert.Equal(alpha, m.HasAlpha);
        Assert.Equal(16, m.Technical.BitDepth);
        Assert.Equal(6, m.Technical.Orientation);
    }

    [Fact]
    public void Metadata_ResizeAndOrientationAllowOnlyDerivedTagChanges()
    {
        var before = PhotoMetadataService.ParseExifToolJson("""[{"IFD0:Orientation":6,"ExifIFD:ExifImageWidth":4000,"ExifIFD:DateTimeOriginal":"2020:01:01 10:00:00","GPS:GPSLatitude":[48,51,24],"XMP-dc:Rights":"Auteur é"}]""");
        var after = PhotoMetadataService.ParseExifToolJson("""[{"IFD0:Orientation":1,"ExifIFD:ExifImageWidth":2000,"ExifIFD:DateTimeOriginal":"2020:01:01 10:00:00","GPS:GPSLatitude":[48,51,24],"XMP-dc:Rights":"Auteur é"}]""");
        PhotoMetadataService.ValidateSnapshot(before, after, "jxl", true);
        Assert.Throws<InvalidDataException>(() => PhotoMetadataService.ValidateSnapshot(before,
            PhotoMetadataService.ParseExifToolJson("""[{"IFD0:Orientation":1,"ExifIFD:DateTimeOriginal":"2020:01:01 10:00:00","XMP-dc:Rights":"Auteur é"}]"""), "jxl", true));
    }

    [Fact]
    public void Metadata_UnrepresentableIptcAndConflictingXmpAreRefused()
    {
        var unsupported = PhotoMetadataService.ParseExifToolJson("""[{"IPTC:ObjectCycle":"p"}]""");
        Assert.NotNull(PhotoMetadataService.PreservationIssue(unsupported, "jxl"));
        Assert.Null(PhotoMetadataService.PreservationIssue(unsupported, "png"));
        var conflict = PhotoMetadataService.ParseExifToolJson("""[{"IPTC:ObjectName":"Titre IPTC","XMP-dc:Title":"Titre XMP"}]""");
        Assert.NotNull(PhotoMetadataService.PreservationIssue(conflict, "avif"));
        var mapped = PhotoMetadataService.ParseExifToolJson("""[{"IPTC:Keywords":["mer","été"]}]""");
        var result = PhotoMetadataService.ParseExifToolJson("""[{"XMP-dc:Subject":["mer","été"]}]""");
        PhotoMetadataService.ValidateSnapshot(mapped, result, "heic", true);
    }

    [Fact]
    public void PhotoValidation_UsesPlannedCodecAndOrientedSize()
    {
        var source = new MediaMetadata(4000, 3000, 0, "JPEG") { Technical = new() { Orientation = 6 } };
        var plan = new CompressionPlan(".avif", "avifenc", [], false, false) { CodecId = "avif", Width = 1500, Height = 2000, NormalizesOrientation = true };
        CompressionVerificationService.ValidatePhotoMetadata(source, new(1500, 2000, 0, "AVIF"), plan);
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidatePhotoMetadata(source, new(1500, 2000, 0, "PNG"), plan));
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidatePhotoMetadata(source, new(2000, 1500, 0, "AVIF"), plan));
    }

    [Fact]
    public void VideoValidation_AcceptsHevcAndDetectsLostAncillaryTracksAndHdr()
    {
        var source = FfprobeService.ParseJson(ProbeJson);
        var plan = new CompressionPlan(".mkv", "ffmpeg", [], false, false) { CodecId = "h265", Width = 3840, Height = 2160 };
        CompressionVerificationService.ValidateVideoMetadata(source, source, plan, true);
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, source with { TotalStreams = 4 }, plan, true));
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, source with { Hdr = false }, plan, true));
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, source with { Technical = source.Technical with { BitDepth = 8 } }, plan, true));
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, source with { DurationMilliseconds = 14000 }, plan, true));
    }

    [Fact]
    public void VideoValidation_ChecksAudioCodecChannelsLayoutAndSync()
    {
        var source = FfprobeService.ParseJson(ProbeJson);
        var plan = new CompressionPlan(".mkv", "ffmpeg", [], false, false)
        {
            CodecId = "hevc", Width = 3840, Height = 2160,
            AudioPlans = [new(1, 0, "opus", 256000, 6, "5.1(side)", false, "Opus")]
        };
        var streams = source.Technical.Streams.Select(s => s.Index == 1 ? s with { Codec = "opus" } : s).ToArray();
        var output = source with { Technical = source.Technical with { Streams = streams } };
        CompressionVerificationService.ValidateVideoMetadata(source, output, plan, true);
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source,
            output with { Technical = output.Technical with { Streams = streams.Select(s => s.Index == 1 ? s with { Channels = 2, ChannelLayout = "stereo" } : s).ToArray() } }, plan, true));
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source,
            output with { Technical = output.Technical with { Streams = streams.Select(s => s.Index == 1 ? s with { StartTimeSeconds = 1 } : s).ToArray() } }, plan, true));
    }

    [Fact]
    public void VideoValidation_RejectsLostSubtitleLanguageAndChangedChapters()
    {
        var source = FfprobeService.ParseJson(ProbeJson);
        var plan = new CompressionPlan(".mkv", "ffmpeg", [], false, false) { CodecId = "hevc" };
        var noLanguage = source with { Technical = source.Technical with { Streams = source.Technical.Streams.Select(s => s.Index == 2 ? s with { Language = "" } : s).ToArray() } };
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, noLanguage, plan, true));
        var noChapter = source with { Technical = source.Technical with { Metadata = new Dictionary<string, string>(source.Technical.Metadata) { ["pixseek:chapters"] = "[]" } } };
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, noChapter, plan, true));
    }

    [Fact]
    public void VideoValidation_RejectsChangedAttachmentAndLostSpatialMetadata()
    {
        var source = FfprobeService.ParseJson(ProbeJson);
        var plan = new CompressionPlan(".mkv", "ffmpeg", [], false, false) { CodecId = "hevc" };
        var changed = source with { Technical = source.Technical with { Streams = source.Technical.Streams.Select(s => s.CodecType == "attachment" ? s with { ExtraDataHash = "SHA256:different" } : s).ToArray() } };
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, changed, plan, true));
        changed = source with { Technical = source.Technical with { Streams = source.Technical.Streams.Select(s => s.Index == 0 ? s with { SideDataJson = "[]" } : s).ToArray() } };
        Assert.Throws<InvalidDataException>(() => CompressionVerificationService.ValidateVideoMetadata(source, changed, plan, true));
    }
}

public sealed class AdaptiveMediaIntegrityIntegrationTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task ActualOpusAndHevcOutput_AreValidatedWithoutHardcodingAv1()
    {
        var sourcePath = Path.Combine(e.Work, "integrity-source-" + Guid.NewGuid().ToString("N") + ".mp4");
        var outputPath = sourcePath + ".mkv";
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"),
            ["-nostdin", "-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=12", "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "1",
             "-c:v", "libx264", "-c:a", "aac", "-metadata:s:a:0", "language=fra", sourcePath]);
        var source = await e.Probe.ProbeAsync(sourcePath);
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"),
            ["-nostdin", "-v", "error", "-n", "-i", sourcePath, "-map", "0", "-map_metadata", "0", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "pools=1:frame-threads=1",
             "-c:a", "libopus", "-b:a", "64000", "-ar", "48000", outputPath]);
        var output = await e.Probe.ProbeAsync(outputPath);
        var audio = source.Technical.Streams.Single(s => s.CodecType == "audio");
        var plan = new CompressionPlan(".mkv", "ffmpeg", [], false, false)
        {
            CodecId = "h265", Width = source.Width, Height = source.Height,
            AudioPlans = [new(audio.Index, 0, "opus", 64000, 1, "mono", false, "Test")]
        };
        CompressionVerificationService.ValidateVideoMetadata(source, output, plan, true);
        var decoded = await e.Video.VerifyDecodeStatisticsAsync(outputPath, default);
        Assert.Equal(12, decoded.Frames);
        Assert.Equal("hevc", output.Codec);
        Assert.Equal("opus", output.Technical.Streams.Single(s => s.CodecType == "audio").Codec);
        var verifier = new CompressionVerificationService(e.Images, e.Probe, e.Video, e.Tools, e.Runner);
        var remuxPath = sourcePath + ".copied.mkv";
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-n", "-i", sourcePath, "-map", "0", "-c:v", "copy", "-c:a", "libopus", remuxPath]);
        var originalHash = await verifier.VideoPacketHashesAsync(sourcePath);
        Assert.Equal(originalHash, await verifier.VideoPacketHashesAsync(remuxPath));
        Assert.NotEqual(originalHash, await verifier.VideoPacketHashesAsync(outputPath));
    }

    [Fact]
    public async Task LosslessVerification_UsesAutoOrientedReferenceRatherThanRawSourceDimensions()
    {
        var source = await e.ImageAsync("orientation-" + Guid.NewGuid().ToString("N") + ".jpg");
        var jpeg = await File.ReadAllBytesAsync(source);
        // A real APP1 EXIF block, with little-endian IFD0 Orientation=6.
        byte[] exif = [0x45, 0x78, 0x69, 0x66, 0, 0, 0x49, 0x49, 0x2a, 0, 8, 0, 0, 0, 1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        var withExif = new byte[jpeg.Length + exif.Length + 4];
        jpeg.AsSpan(0, 2).CopyTo(withExif);
        withExif[2] = 0xff; withExif[3] = 0xe1; withExif[4] = 0; withExif[5] = (byte)(exif.Length + 2);
        exif.CopyTo(withExif, 6); jpeg.AsSpan(2).CopyTo(withExif.AsSpan(6 + exif.Length));
        await File.WriteAllBytesAsync(source, withExif);
        var original = await e.Images.IdentifyAsync(source);
        Assert.Equal(6, original.Technical.Orientation);
        var reference = source + ".reference.png"; var output = source + ".jxl";
        await e.Images.RunAsync([source, "-auto-orient", "-strip", reference]);
        await e.Runner.CheckedAsync(e.Tools.PathFor("cjxl"), [reference, output, "-d", "0", "-e", "3"]);
        var plan = new CompressionPlan(".jxl", "cjxl", [], true, false) { CodecId = "jxl", Width = 240, Height = 320, NormalizesOrientation = true, ConvertsToSrgb = true };
        var request = new EncodingRequest(source, output, MediaType.Image, original, new() { KeepMetadata = false });
        var verifier = new CompressionVerificationService(e.Images, e.Probe, e.Video, e.Tools, e.Runner);
        var verified = await verifier.VerifyAsync(request, new PreparedCompressionPlan { Command = plan, SourceMetadata = original, ReferencePath = reference }, default);
        Assert.Contains("Pixels décodés identiques", verified);
    }

    [Fact]
    public async Task LosslessVerification_ChecksActualAlphaAndIccAndRejectsAlteredAlpha()
    {
        var source = Path.Combine(e.Work, "integrity-alpha-" + Guid.NewGuid().ToString("N") + ".jxl");
        var output = source + ".jxl";
        var png = source + ".png";
        await e.Images.RunAsync(["-size", "64x48", "xc:none", "-fill", "#ff003380", "-draw", "rectangle 8,8 50,40", "-profile", Path.Combine(e.Tools.NativeDirectory, "sRGB.icc"), png]);
        await e.Runner.CheckedAsync(e.Tools.PathFor("cjxl"), [png, source, "-d", "0", "-e", "3"]);
        var original = await e.Images.IdentifyAsync(source);
        await e.Runner.CheckedAsync(e.Tools.PathFor("cjxl"), [source, output, "-d", "0", "-e", "3"]);
        var plan = new CompressionPlan(".jxl", "cjxl", [], true, false) { CodecId = "jxl", Width = 64, Height = 48, NormalizesOrientation = true };
        var request = new EncodingRequest(source, output, MediaType.Image, original, new());
        var verifier = new CompressionVerificationService(e.Images, e.Probe, e.Video, e.Tools, e.Runner);
        Assert.Contains("Pixels décodés identiques", await verifier.VerifyAsync(request, plan, default));
        var wrongAlpha = source + ".wrong.png";
        await e.Images.RunAsync([source, "-channel", "A", "-evaluate", "multiply", "0.5", "+channel", wrongAlpha]);
        File.Delete(output);
        await e.Runner.CheckedAsync(e.Tools.PathFor("cjxl"), [wrongAlpha, output, "-d", "0", "-e", "3"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.VerifyAsync(request, plan, default));
    }
}
