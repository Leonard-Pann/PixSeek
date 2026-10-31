using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PixSeek.Core;

namespace PixSeek.Media;
public sealed class FfprobeService(NativeTools tools, ProcessRunner runner)
{
    public static (int Width, int Height) DisplaySize(MediaMetadata metadata)
    {
        var width = (double)Math.Max(1, metadata.Width);
        var height = Math.Max(1, metadata.Height);
        var rotation = (double)metadata.Technical.Rotation;
        var aspect = metadata.Technical.SampleAspectRatio;
        if (string.IsNullOrEmpty(aspect) && metadata.RawJson != null)
        {
            using var document = JsonDocument.Parse(metadata.RawJson);
            var stream = document.RootElement.GetProperty("streams").EnumerateArray()
                .First(s => Text(s, "codec_type") == "video" && (!s.TryGetProperty("disposition", out var d) || Number(d, "attached_pic") == 0));
            aspect = Text(stream, "sample_aspect_ratio");
            rotation = Rotation(stream);
        }
        if (Rational(aspect) is > 0 and var ratio) width *= ratio;
        var w = (int)Math.Clamp(Math.Round(width), 1, 32768);
        return Math.Abs((int)Math.Round(rotation / 90)) % 2 == 1 ? (height, w) : (w, height);
    }

    public async Task<MediaMetadata> ProbeAsync(string file, CancellationToken ct = default)
    {
        var json = await runner.CheckedAsync(tools.PathFor("ffprobe"), ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-show_data_hash", "sha256", "-of", "json", file], ct, timeout: TimeSpan.FromMinutes(2));
        return ParseJson(json);
    }

    public static MediaMetadata ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streamArray) || streamArray.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Liste des flux absente du diagnostic FFprobe.");
        var streams = streamArray.EnumerateArray().ToArray();
        var parsedStreams = streams.Select(ParseStream).ToArray();
        var videos = streams.Where(s => Text(s, "codec_type") == "video" && (!s.TryGetProperty("disposition", out var d) || Number(d, "attached_pic") == 0)).ToArray();
        if (videos.Length == 0)
            throw new InvalidDataException("Aucun flux vidéo décodable.");
        var v = videos[0];
        doc.RootElement.TryGetProperty("format", out var format);
        var seconds = Numeric(format, "duration") ?? Numeric(v, "duration") ??
            parsedStreams.Where(s => s.CodecType is "video" or "audio").Select(s => s.DurationSeconds ?? 0).DefaultIfEmpty().Max();
        var pixelFormat = Text(v, "pix_fmt");
        var technicalTags = Tags(format);
        if (doc.RootElement.TryGetProperty("chapters", out var chapters)) technicalTags["pixseek:chapters"] = chapters.GetRawText();
        var sideData = v.TryGetProperty("side_data_list", out var sides) ? sides.GetRawText() : "";
        var transfer = Text(v, "color_transfer");
        return new(Number(v, "width"), Number(v, "height"), (long)Math.Clamp(Math.Round(seconds * 1000), 0, long.MaxValue),
            Text(v, "codec_name"), HasAlphaPixelFormat(pixelFormat), Math.Max(1, Number(v, "nb_frames")),
            parsedStreams.Count(s => s.CodecType == "audio"), parsedStreams.Count(s => s.CodecType == "subtitle"), streams.Length,
            transfer is "smpte2084" or "arib-std-b67", json)
        {
            Technical = new()
            {
                Container = Text(format, "format_name"), PixelFormat = pixelFormat, BitDepth = BitDepth(v),
                ChromaSubsampling = Subsampling(pixelFormat), ColorPrimaries = Text(v, "color_primaries"), ColorTransfer = transfer,
                ColorSpace = Text(v, "color_space"), ColorRange = Text(v, "color_range"), FrameRate = Text(v, "r_frame_rate"),
                AverageFrameRate = Text(v, "avg_frame_rate"), TimeBase = Text(v, "time_base"),
                SampleAspectRatio = Text(v, "sample_aspect_ratio"), DisplayAspectRatio = Text(v, "display_aspect_ratio"),
                Rotation = (int)Math.Round(Rotation(v)), Interlaced = Text(v, "field_order") is "tt" or "bb" or "tb" or "bt",
                HasGainMap = sideData.Contains("gain map", StringComparison.OrdinalIgnoreCase),
                HasDynamicHdr = sideData.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || sideData.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase) ||
                    sideData.Contains("SMPTE2094", StringComparison.OrdinalIgnoreCase) || sideData.Contains("Dynamic HDR", StringComparison.OrdinalIgnoreCase) || sideData.Contains("HDR10+", StringComparison.OrdinalIgnoreCase),
                Streams = parsedStreams, Metadata = technicalTags
            }
        };
    }

    private static MediaStreamMetadata ParseStream(JsonElement stream)
    {
        var tags = Tags(stream);
        var dispositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object)
            foreach (var p in disposition.EnumerateObject()) dispositions[p.Name] = Number(disposition, p.Name);
        var duration = Numeric(stream, "duration");
        if (duration == null && Numeric(stream, "duration_ts") is { } ticks && Rational(Text(stream, "time_base")) is > 0 and var timeBase)
            duration = ticks * timeBase;
        // Matroska often reports duration in a tag instead of the stream field.
        if (duration == null && tags.TryGetValue("DURATION", out var durationTag) && TimeSpan.TryParse(durationTag, CultureInfo.InvariantCulture, out var time))
            duration = time.TotalSeconds;
        return new()
        {
            Index = Number(stream, "index"), CodecType = Text(stream, "codec_type"), Codec = Text(stream, "codec_name"),
            PixelFormat = Text(stream, "pix_fmt"), BitDepth = BitDepth(stream), ChannelLayout = Text(stream, "channel_layout"),
            Channels = Number(stream, "channels"), SampleRate = Number(stream, "sample_rate"),
            BitRate = long.TryParse(Text(stream, "bit_rate"), CultureInfo.InvariantCulture, out var bitrate) ? bitrate : null,
            Language = tags.GetValueOrDefault("language", ""), Title = tags.GetValueOrDefault("title", ""),
            Disposition = dispositions, Tags = tags, SideDataJson = stream.TryGetProperty("side_data_list", out var sideData) ? sideData.GetRawText() : null,
            AttachedPicture = dispositions.GetValueOrDefault("attached_pic") != 0,
            ExtraDataHash = Text(stream, "extradata_hash"),
            TimeBase = Text(stream, "time_base"), StartTimeSeconds = Numeric(stream, "start_time") ?? 0, DurationSeconds = duration
        };
    }

    private static Dictionary<string, string> Tags(JsonElement parent)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            foreach (var p in tags.EnumerateObject()) result[p.Name] = p.Value.ToString();
        return result;
    }

    public static double? Rational(string value)
    {
        var p = value.Split(['/', ':']);
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0 && double.IsFinite(n / d)) return n / d;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }

    public static bool HasAlphaPixelFormat(string format) => format.StartsWith("yuva", StringComparison.Ordinal) ||
        format.StartsWith("gbrap", StringComparison.Ordinal) || format.StartsWith("rgba", StringComparison.Ordinal) ||
        format.StartsWith("bgra", StringComparison.Ordinal) || format.StartsWith("argb", StringComparison.Ordinal) ||
        format.StartsWith("abgr", StringComparison.Ordinal) || format.StartsWith("ya", StringComparison.Ordinal);

    private static int BitDepth(JsonElement stream)
    {
        var explicitDepth = Number(stream, "bits_per_raw_sample");
        if (explicitDepth > 0) return explicitDepth;
        var format = Text(stream, "pix_fmt");
        if (format.Length == 0) return 0;
        if (format.Contains("f32", StringComparison.Ordinal)) return 32;
        if (format.Contains("rgb48", StringComparison.Ordinal) || format.Contains("rgba64", StringComparison.Ordinal) || format.Contains("gray16", StringComparison.Ordinal)) return 16;
        if (format.StartsWith("p010", StringComparison.Ordinal) || format.StartsWith("x2rgb10", StringComparison.Ordinal)) return 10;
        if (format.StartsWith("p012", StringComparison.Ordinal)) return 12;
        if (format.StartsWith("p016", StringComparison.Ordinal)) return 16;
        var match = System.Text.RegularExpressions.Regex.Match(format, @"(?:p|gray|ya)(9|10|12|14|16)(?:le|be)?$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 8;
    }

    private static string Subsampling(string format) => format.Contains("420", StringComparison.Ordinal) || format is "nv12" or "nv21" || format.StartsWith("p01", StringComparison.Ordinal) ? "420" :
        format.Contains("422", StringComparison.Ordinal) || format is "yuyv422" or "uyvy422" ? "422" :
        format.StartsWith("gray", StringComparison.Ordinal) ? "400" : format.Length > 0 ? "444" : "";

    private static double Rotation(JsonElement stream)
    {
        var rotation = stream.TryGetProperty("tags", out var tags) ? Numeric(tags, "rotate") ?? 0 : 0;
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            foreach (var side in sideData.EnumerateArray()) if (Numeric(side, "rotation") is { } angle) rotation = angle;
        return rotation;
    }

    private static double? Numeric(JsonElement e, string name) => double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    private static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p.ToString() : "";
    private static int Number(JsonElement e, string name) => int.TryParse(Text(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

public sealed class ImageMagickService(NativeTools tools, ProcessRunner runner) : IImageTensorSource
{
    public Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct = default) => runner.CheckedAsync(tools.PathFor("magick"), args, ct, timeout: TimeSpan.FromMinutes(15));
    public async Task<MediaMetadata> IdentifyAsync(string file, CancellationToken ct = default)
    {
        var output = await RunAsync(["identify", "-ping", "-format", "%w|%h|%m|%[channels]|%n|%z|%[colorspace]|%[orientation]\n", file], ct);
        return ParseIdentification(output);
    }

    public static MediaMetadata ParseIdentification(string output)
    {
        var parts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('\r').Split('|') ?? [];
        if (parts.Length < 5)
            throw new InvalidDataException("Métadonnées image invalides.");
        var channels = parts[3].Split(' ')[0].ToLowerInvariant();
        var alpha = channels.EndsWith('a') || channels.Contains("alpha", StringComparison.Ordinal);
        var orientation = parts.Length > 7 ? ParseOrientation(parts[7]) : 1;
        return new(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), 0,
            parts[2], alpha, int.Parse(parts[4], CultureInfo.InvariantCulture))
        {
            Technical = new()
            {
                Container = parts[2], PixelFormat = parts[3], Orientation = orientation,
                BitDepth = parts.Length > 5 && int.TryParse(parts[5], out var depth) ? depth : 8,
                ColorSpace = parts.Length > 6 ? parts[6] : ""
            }
        };
    }

    public static int ParseOrientation(string orientation) => orientation.Trim() switch
    {
        "TopLeft" or "1" => 1, "TopRight" or "2" => 2, "BottomRight" or "3" => 3, "BottomLeft" or "4" => 4,
        "LeftTop" or "5" => 5, "RightTop" or "6" => 6, "RightBottom" or "7" => 7, "LeftBottom" or "8" => 8, _ => 1
    };

    public Task ThumbnailAsync(string file, string destination, int size, CancellationToken ct = default) => RunAsync([file + "[0]", "-auto-orient", "-thumbnail", $"{size}x{size}>", "-strip", "-quality", "85", destination], ct);
    public async Task<byte[]> ReadModelRgbAsync(string file, CancellationToken ct, bool squash = false)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pixseek-{Guid.NewGuid():N}.rgb");
        try
        {
            await RunAsync([file + "[0]", "-auto-orient", "-colorspace", "sRGB", "-background", "white", "-alpha", "remove", "-filter", "Catrom", "-resize", squash ? "224x224!" : "224x224^", "-gravity", "center", "-extent", "224x224", "-depth", "8", "RGB:" + tmp], ct);
            return await File.ReadAllBytesAsync(tmp, ct);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public async Task<byte[]> ReadFaceRgbAsync(string file, int size, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "pixseek-face-" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            await RunAsync([file + "[0]", "-auto-orient", "-colorspace", "sRGB", "-background", "black", "-alpha", "remove", "-resize", $"{size}x{size}>", "-gravity", "northwest", "-extent", $"{size}x{size}", "-depth", "8", "RGB:" + temporary], ct);
            return await File.ReadAllBytesAsync(temporary, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task WriteFaceThumbnailAsync(byte[] rgb, int size, string destination, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "pixseek-face-" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            await File.WriteAllBytesAsync(temporary, rgb, ct);
            await RunAsync(["-size", $"{size}x{size}", "-depth", "8", "RGB:" + temporary, "-quality", "88", destination], ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string> PixelSignatureAsync(string file, CancellationToken ct) => (await RunAsync([file, "-format", "%#\n", "info:"], ct)).Trim();
    public async Task<string> OrientedPixelSignatureAsync(string file, CancellationToken ct) => (await RunAsync([file, "-auto-orient", "-format", "%#\n", "info:"], ct)).Trim();
    public async Task<string> AlphaSignatureAsync(string file, bool orient, CancellationToken ct)
    {
        var args = new List<string> { file };
        if (orient) args.Add("-auto-orient");
        args.AddRange(["-alpha", "extract", "-format", "%#\n", "info:"]);
        return (await RunAsync(args, ct)).Trim();
    }
    public async Task<IReadOnlyDictionary<string, string>> CriticalMetadataAsync(string file, CancellationToken ct)
    {
        // EXIF orientation is handled separately by checking decoded dimensions/pixels.
        var output = await RunAsync([file + "[0]", "-format", "%[profiles]\n%[EXIF:DateTimeOriginal]\n", "info:"], ct);
        var result = new Dictionary<string, string>();
        var lines = output.Split('\n');
        result["profiles"] = lines[0].Trim();
        result["captureDate"] = lines.Length > 1 ? lines[1].Trim() : "";
        result["iccHash"] = "";
        if (result["profiles"].Contains("icc", StringComparison.OrdinalIgnoreCase))
        {
            var temp = Path.Combine(Path.GetTempPath(), "pixseek-icc-" + Guid.NewGuid().ToString("N") + ".icc");
            try
            {
                await RunAsync([file + "[0]", "ICC:" + temp], ct);
                await using var profile = File.OpenRead(temp);
                result["iccHash"] = Convert.ToHexString(await SHA256.HashDataAsync(profile, ct));
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        return result;
    }
}

public sealed class FfmpegService(NativeTools tools, ProcessRunner runner)
{
    public async Task ExtractFrameAsync(string file, long timestamp, string destination, int maxSize = 640, CancellationToken ct = default)
    {
        await runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-ss", (timestamp / 1000d).ToString("0.###", CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1", "-vf", $"scale={maxSize}:{maxSize}:force_original_aspect_ratio=decrease", "-update", "1", destination], ct, timeout: TimeSpan.FromMinutes(2));
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
            throw new InvalidDataException("Image vidéo non extraite.");
    }

    public static IReadOnlyList<long> FrameTimestamps(long duration, int intervalSeconds, int maxFrames)
    {
        if (duration <= 0)
            return[0];
        intervalSeconds = Math.Clamp(intervalSeconds, 1, 600);
        maxFrames = Math.Clamp(maxFrames, 1, 1000);
        var count = Math.Min(maxFrames, Math.Max(1, (int)Math.Ceiling(duration / (intervalSeconds * 1000d))));
        if (count == 1)
            return[Math.Min(duration / 2, Math.Max(0, duration - 100))];
        var step = Math.Max(intervalSeconds * 1000d, (duration - 100d) / (count - 1));
        return Enumerable.Range(0, count).Select(i => (long)Math.Clamp(i * step, 0, Math.Max(0, duration - 100))).ToArray();
    }

    public Task VerifyDecodeAsync(string file, CancellationToken ct) => VerifyDecodeStatisticsAsync(file, ct);
    public async Task<(int Frames, long DurationMilliseconds)> VerifyDecodeStatisticsAsync(string file, CancellationToken ct)
    {
        var output = await runner.CheckedAsync(tools.PathFor("ffmpeg"),
            ["-nostdin", "-v", "error", "-xerror", "-i", file, "-map", "0:v", "-map", "0:a?", "-fps_mode", "passthrough", "-progress", "pipe:1", "-nostats", "-f", "null", "-"], ct);
        var frames = 0; long micros = 0;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) && int.TryParse(line[6..].Trim(), out var frame)) frames = frame;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..].Trim(), out var time)) micros = time;
        }
        return (frames, micros / 1000);
    }
}

public sealed class ThumbnailService(IPlatformPaths paths, ImageMagickService images, FfmpegService videos)
{
    public string? FindCachedPath(MediaFile media)
    {
        if (IsCachedFile(media.ThumbnailPath)) return media.ThumbnailPath;
        var canonical = CachePath(media);
        return IsCachedFile(canonical) ? canonical : null;
    }
    private static bool IsCachedFile(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && new FileInfo(path) is { Exists: true, Length: > 0 }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    public string CachePath(MediaFile media, string suffix = "thumb")
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{media.FullPath}|{media.SizeBytes}|{media.LastModifiedAt.Ticks}|v2|{suffix}")));
        return Path.Combine(paths.Cache, key + ".jpg");
    }

    public async Task<string> CreateAsync(MediaFile media, CancellationToken ct)
    {
        var target = CachePath(media);
        if (IsCachedFile(target))
            return target;
        var temp = target + ".partial.jpg";
        try
        {
            if (media.MediaType == MediaType.Image)
                await images.ThumbnailAsync(media.FullPath, temp, 480, ct);
            else
                await videos.ExtractFrameAsync(media.FullPath, Math.Min(media.DurationMilliseconds / 2, 1000), temp, 480, ct);
            File.Move(temp, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public void Trim(long maxBytes, IReadOnlySet<string> protectedPaths)
    {
        var files = new DirectoryInfo(paths.Cache).EnumerateFiles().OrderBy(f => f.LastAccessTimeUtc).ToArray();
        var total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= maxBytes)
                break;
            if (protectedPaths.Contains(f.FullName))
                continue;
            try
            {
                var n = f.Length;
                f.Delete();
                total -= n;
            }
            catch (IOException)
            {
            }
        }
    }
}
