using PixSeek.Core;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;

public sealed class MultiTrackExportTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Theory]
    [InlineData("h264", "libx264", 25)]
    [InlineData("hevc", "libx265", 28)]
    [InlineData("av1", "libsvtav1", 35)]
    [InlineData("vp9", "libvpx-vp9", 35)]
    public async Task ReencodedVideo_RetainsEveryAudioSubtitleAttachmentAndTag(string codec, string encoder, double quality)
    {
        var work = Path.Combine(e.Work, "multi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var fr = Path.Combine(work, "fr.srt"); var en = Path.Combine(work, "en.srt"); var chapter = Path.Combine(work, "chapters.txt"); var attachment = Path.Combine(work, "notes.txt");
        await File.WriteAllTextAsync(fr, "1\n00:00:00,000 --> 00:00:00,900\nBonjour !\n");
        await File.WriteAllTextAsync(en, "1\n00:00:00,000 --> 00:00:00,900\nHello!\n");
        await File.WriteAllTextAsync(chapter, ";FFMETADATA1\ntitle=All metadata\nartist=PixSeek test\ncomment=Keep this comment\ncreation_time=2020-01-02T03:04:05Z\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=900\ntitle=Chapitre conservé\n");
        await File.WriteAllTextAsync(attachment, "Attachment that must survive.");
        var source = Path.Combine(work, "input.mkv");
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-i", fr, "-i", en, "-f", "ffmetadata", "-i", chapter,
            "-t", "1", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map", "4:s", "-map_metadata", "5", "-map_chapters", "5", "-c:v", "libx264", "-threads", "2", "-c:a", "aac", "-ac:a", "2", "-c:s", "srt",
            "-metadata:s:a:0", "language=fra", "-metadata:s:a:0", "title=Français", "-metadata:s:a:1", "language=eng", "-metadata:s:a:1", "title=Commentary", "-disposition:a:0", "default", "-disposition:a:1", "comment",
            "-metadata:s:s:0", "language=fra", "-metadata:s:s:1", "language=eng", "-attach", attachment, "-metadata:s:t:0", "mimetype=text/plain", "-metadata:s:t:0", "filename=notes.txt", source]);
        var before = await e.Probe.ProbeAsync(source);
        var result = await e.Encoder.EncodeAsync(new(source, Path.Combine(work, "output.mkv"), MediaType.Video, before,
            new() { VideoCodec = codec, VideoEncoderId = encoder, VideoNativeQuality = quality, VideoRateControl = "crf", KeepOnlySmaller = false, KeepMetadata = false, Speed = 12 }), new Progress<EncodingProgress>(), default);
        Assert.True(result.Status == "Converti", result.Error ?? result.Verification);
        var after = await e.Probe.ProbeAsync(result.DestinationPath!);
        Assert.Equal(codec, after.Codec); Assert.Equal(2, after.AudioStreams); Assert.Equal(2, after.SubtitleStreams); Assert.Equal(before.TotalStreams, after.TotalStreams);
        Assert.All(after.Technical.Streams.Where(s => s.CodecType == "audio"), s => Assert.Equal("opus", s.Codec));
        var originalAudio = before.Technical.Streams.Where(s => s.CodecType == "audio").ToArray();
        var outputAudio = after.Technical.Streams.Where(s => s.CodecType == "audio").ToArray();
        for (var i = 0; i < 2; i++) { Assert.Equal(originalAudio[i].Language, outputAudio[i].Language); Assert.Equal(originalAudio[i].Title, outputAudio[i].Title); Assert.Equal(originalAudio[i].Channels, outputAudio[i].Channels); }
        Assert.Equal("All metadata", after.Technical.Metadata["title"]); Assert.Equal("Keep this comment", after.Technical.Metadata["COMMENT"]);
        var sourceAttachment = Assert.Single(before.Technical.Streams, s => s.CodecType == "attachment");
        var outputAttachment = Assert.Single(after.Technical.Streams, s => s.CodecType == "attachment");
        Assert.NotEmpty(sourceAttachment.ExtraDataHash!); Assert.Equal(sourceAttachment.ExtraDataHash, outputAttachment.ExtraDataHash);
        Assert.Equal(await OriginalMediaExporter.HashAsync(source), result.SourceSha256); Assert.True(await OriginalMediaExporter.IsVerifiedAsync(result));
    }
    [Fact]
    public async Task UnsupportedImage_StillPreservesEveryMetadataByteByCopy()
    {
        var file = Path.Combine(e.Work, "metadata-" + Guid.NewGuid().ToString("N") + ".png");
        await e.Images.RunAsync(["-size", "80x60", "gradient:navy-orange", "-set", "Comment", "A private PNG metadata value", file]);
        var output = Path.Combine(e.Work, "copy-" + Guid.NewGuid().ToString("N") + ".png");
        var result = await e.Encoder.EncodeAsync(new(file, output, MediaType.Image, await e.Images.IdentifyAsync(file),
            new() { ImageCodec = "avif", ImageEncoderId = "missing-encoder", KeepMetadata = false }), new Progress<EncodingProgress>(), default);
        Assert.Equal("Copié", result.Status); Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(result.DestinationPath!));
    }
    [Fact]
    public async Task PhotoMetadataNotTransportedByTarget_ExportsExactOriginal()
    {
        var file = Path.Combine(e.Work, "commentaire été-" + Guid.NewGuid().ToString("N") + ".png");
        await e.Images.RunAsync(["-size", "80x60", "gradient:navy-orange", "-set", "Comment", "Private PNG comment", file]);
        var result = await e.Encoder.EncodeAsync(new(file, file + ".jxl", MediaType.Image, await e.Images.IdentifyAsync(file),
            new() { ImageCodec = "jxl", ImageNativeQuality = 0, KeepOnlySmaller = false }), new Progress<EncodingProgress>(), default);
        Assert.Equal("Copié", result.Status);
        Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(result.DestinationPath!));
        Assert.Contains("métadonnée", result.Verification, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ContainerMetadataNotRepresentableInMkv_FallsBackToExactOriginal()
    {
        var directory = Path.Combine(e.Work, "xmp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mp4");
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=12", "-t", "1", "-c:v", "libx264", "-threads", "2", source]);
        await e.Runner.CheckedAsync(e.Tools.PathFor("exiftool"), ["-overwrite_original", "-XMP-dc:Description=Private XMP that must survive", source]);
        var result = await e.Encoder.EncodeAsync(new(source, Path.Combine(directory, "output.mkv"), MediaType.Video, await e.Probe.ProbeAsync(source),
            new() { VideoCodec = "h264", VideoEncoderId = "libx264", VideoNativeQuality = 28, KeepOnlySmaller = false }), new Progress<EncodingProgress>(), default);
        Assert.Equal("Copié", result.Status); Assert.EndsWith(".mp4", result.DestinationPath);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(result.DestinationPath!));
    }
}
