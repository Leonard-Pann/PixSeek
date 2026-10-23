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
        var rotation = 0d;
        if (metadata.RawJson != null)
        {
            using var document = JsonDocument.Parse(metadata.RawJson);
            var stream = document.RootElement.GetProperty("streams").EnumerateArray()
                .First(s => Text(s, "codec_type") == "video" && (!s.TryGetProperty("disposition", out var d) || Number(d, "attached_pic") == 0));
            var ratio = Text(stream, "sample_aspect_ratio").Split(':');
            if (ratio.Length == 2 && double.TryParse(ratio[0], CultureInfo.InvariantCulture, out var numerator) &&
                double.TryParse(ratio[1], CultureInfo.InvariantCulture, out var denominator) && numerator > 0 && denominator > 0)
                width *= numerator / denominator;
            if (stream.TryGetProperty("tags", out var tags))
                double.TryParse(Text(tags, "rotate"), CultureInfo.InvariantCulture, out rotation);
            if (stream.TryGetProperty("side_data_list", out var sideData))
                foreach (var side in sideData.EnumerateArray())
                    if (side.TryGetProperty("rotation", out var angle) && angle.TryGetDouble(out var value)) rotation = value;
        }
        var w = (int)Math.Clamp(Math.Round(width), 1, 32768);
        return Math.Abs((int)Math.Round(rotation / 90)) % 2 == 1 ? (height, w) : (w, height);
    }

    public async Task<MediaMetadata> ProbeAsync(string file, CancellationToken ct = default)
    {
        var json = await runner.CheckedAsync(tools.PathFor("ffprobe"), ["-v", "error", "-show_format", "-show_streams", "-of", "json", file], ct, timeout: TimeSpan.FromMinutes(2));
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var videos = streams.Where(s => Text(s, "codec_type") == "video" && (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var a) || a.GetInt32() == 0)).ToArray();
        if (videos.Length == 0)
            throw new InvalidDataException("Aucun flux vidéo décodable.");
        var v = videos[0];
        var duration = doc.RootElement.TryGetProperty("format", out var f) ? Text(f, "duration") : Text(v, "duration");
        double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds);
        return new(Number(v, "width"), Number(v, "height"), (long)(seconds * 1000), Text(v, "codec_name"), false, 1, streams.Count(s => Text(s, "codec_type") == "audio"), streams.Count(s => Text(s, "codec_type") == "subtitle"), streams.Length, Text(v, "color_transfer")is "smpte2084" or "arib-std-b67", json);
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.ToString() : "";
    private static int Number(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 0;
}

public sealed class ImageMagickService(NativeTools tools, ProcessRunner runner) : IImageTensorSource
{
    public Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct = default) => runner.CheckedAsync(tools.PathFor("magick"), args, ct, timeout: TimeSpan.FromMinutes(15));
    public async Task<MediaMetadata> IdentifyAsync(string file, CancellationToken ct = default)
    {
        var output = await RunAsync(["identify", "-ping", "-format", "%w|%h|%m|%[channels]|%n\n", file], ct);
        var parts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).First().Split('|');
        if (parts.Length < 5)
            throw new InvalidDataException("Métadonnées image invalides.");
        return new(int.Parse(parts[0]), int.Parse(parts[1]), 0, parts[2], parts[3].Contains('a'), int.Parse(parts[4]));
    }

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

    public Task VerifyDecodeAsync(string file, CancellationToken ct) => runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-xerror", "-i", file, "-map", "0:v", "-map", "0:a?", "-f", "null", "-"], ct);
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
