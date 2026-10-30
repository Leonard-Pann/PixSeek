using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Build/runtime support is only a hint: availability requires a real encode/decode.</summary>
public sealed class EncoderCapabilityService(NativeTools tools, ProcessRunner runner)
{
    private const int Schema = 5;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly StringBuilder details = new();

    public async Task<ToolCapabilities> DetectAsync(CancellationToken ct = default)
    {
        var fingerprint = CacheKey();
        Directory.CreateDirectory(tools.CacheDirectory);
        var cache = Path.Combine(tools.CacheDirectory, "encoder-capabilities-v5.json");
        try
        {
            if (File.Exists(cache))
            {
                var saved = JsonSerializer.Deserialize<CapabilityCache>(await File.ReadAllTextAsync(cache, ct));
                if (saved?.Schema == Schema && saved.Fingerprint == fingerprint && DateTimeOffset.UtcNow - saved.CheckedAt < TimeSpan.FromHours(6))
                    return saved.Capabilities;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { details.AppendLine($"Capability cache ignored: {e.Message}"); }

        var ff = await Describe("ffmpeg", ["-version"], ct);
        var im = await Describe("magick", ["-version"], ct);
        var list = await Describe("ffmpeg", ["-hide_banner", "-encoders"], ct);
        await DeviceFingerprint(ct);
        var work = Path.Combine(tools.CacheDirectory, "capability-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var rows = new List<EncoderDescriptor>();
            foreach (var descriptor in VideoCatalog())
            {
                ct.ThrowIfCancellationRequested();
                var present = Regex.IsMatch(list, @"(?m)^\s*V[^\r\n]*\s" + Regex.Escape(descriptor.Id) + @"\s");
                if (!present) { rows.Add(descriptor with { UnavailableReason = "Encoder absent from the bundled FFmpeg build.", Version = FirstLine(ff) }); continue; }
                if (!PlatformSupports(descriptor)) { rows.Add(descriptor with { UnavailableReason = "Backend unavailable on this operating system.", Version = FirstLine(ff) }); continue; }
                var help = await Describe("ffmpeg", ["-hide_banner", "-h", "encoder=" + descriptor.Id], ct);
                var formats = Regex.Match(help, @"Supported pixel formats:\s*([^\r\n]+)");
                var row = descriptor with { PixelFormats = formats.Success ? formats.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : descriptor.PixelFormats, Version = FirstLine(ff) };
                var reason = await ProbeVideo(row, work, ct);
                rows.Add(row with { Available = reason == null, UnavailableReason = reason });
            }

            var png = Path.Combine(work, "reference.png");
            var pngReason = await Attempt("magick", ["-size", "128x96", "gradient:#204060-#efc080", png], ct);
            rows.Add(new() { Id = "png", CodecId = CompressionCodecs.Png, Tool = "magick", QualityName = "Compression level", Minimum = 0, Maximum = 9, LowerIsBetter = false, DefaultQuality = 6, RateControl = "lossless", Available = pngReason == null, UnavailableReason = pngReason, Version = FirstLine(im), PixelFormats = ["rgba", "rgba64be"] });
            var jxl = await ProbeImage("cjxl", "djxl", png, Path.Combine(work, "probe.jxl"), [png, Path.Combine(work, "probe.jxl"), "-d", "1", "-e", "3", "--num_threads=2"], [Path.Combine(work, "probe.jxl"), Path.Combine(work, "jxl-decoded.png")], ct);
            rows.Add(new() { Id = "cjxl", CodecId = CompressionCodecs.Jxl, Tool = "cjxl", QualityName = "Distance", Minimum = 0, Maximum = 25, Step = .1, DefaultQuality = 1, RateControl = "distance", Available = jxl == null, UnavailableReason = jxl, Version = FirstLine(await Describe("cjxl", ["--version"], ct)), PixelFormats = ["rgb", "rgba", "rgb16", "rgba16"] });
            var avif = await ProbeImage("avifenc", "avifdec", png, Path.Combine(work, "probe.avif"), ["--jobs", "2", "--speed", "8", "--qcolor", "70", png, Path.Combine(work, "probe.avif")], [Path.Combine(work, "probe.avif"), Path.Combine(work, "avif-decoded.png")], ct);
            rows.Add(new() { Id = "avifenc", CodecId = CompressionCodecs.Avif, Tool = "avifenc", QualityName = "Quality", Minimum = 0, Maximum = 100, LowerIsBetter = false, DefaultQuality = 70, RateControl = "quality", Available = avif == null, UnavailableReason = avif, Version = FirstLine(await Describe("avifenc", ["--version"], ct)), PixelFormats = ["yuv444", "yuv420", "yuv444p10", "rgba"] });
            var heic = await ProbeImage("heif-enc", "heif-dec", png, Path.Combine(work, "probe.heic"), ["-q", "75", "-o", Path.Combine(work, "probe.heic"), png], [Path.Combine(work, "probe.heic"), Path.Combine(work, "heic-decoded.png")], ct);
            rows.Add(new() { Id = "heif-enc", CodecId = CompressionCodecs.Heic, Tool = "heif-enc", QualityName = "Quality", Minimum = 0, Maximum = 100, LowerIsBetter = false, DefaultQuality = 75, RateControl = "quality", Available = heic == null, UnavailableReason = heic, Version = FirstLine(await Describe("heif-enc", ["--version"], ct)), PixelFormats = ["rgb", "rgba", "rgb16", "rgba16"] });
            var ssim = pngReason == null && await ProbeMetric("ssimulacra2", [png, png], 99, 101, ct);
            var butter = pngReason == null && await ProbeMetric("butteraugli", [png, png], 0, .001, ct);
            var model1080 = tools.ModelPath("vmaf_v1.0.16_3d0h.json");
            var model4k = tools.ModelPath("vmaf_v1.0.16_1d5h_2160.json");
            var vmaf = model1080 != null && model4k != null && await ProbeVmaf(work, model1080, model4k, ct);
            var result = new ToolCapabilities(FirstLine(ff), FirstLine(im), rows.FirstOrDefault(x => x.Available && x.CodecId == CompressionCodecs.Av1)?.Id, jxl == null, avif == null, tools.NativeDirectory, details.ToString())
            {
                Encoders = rows, Heic = heic == null, ImageMetrics = ssim, Butteraugli = butter, VideoMetrics = vmaf,
                Vmaf1080Model = model1080, Vmaf4kModel = model4k, Fingerprint = fingerprint
            };
            try
            {
                var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CapabilityCache(Schema, fingerprint, DateTimeOffset.UtcNow, result), Json), ct);
                File.Move(temporary, cache, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { details.AppendLine($"Capability cache could not be written: {e.Message}"); }
            return result;
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private async Task<string?> ProbeVideo(EncoderDescriptor row, string work, CancellationToken ct)
    {
        var quantizers = row.Maximum > 100 ? new[] { 60d, 112d, 180d } : row.LowerIsBetter ? new[] { 16d, 28d, 42d } : new[] { 80d, 60d, 35d };
        var sizes = new List<long>();
        for (var i = 0; i < quantizers.Length; ++i)
        {
            var file = Path.Combine(work, row.Id + "-" + i + ".mkv");
            var args = new List<string> { "-hide_banner", "-loglevel", "verbose", "-nostdin", "-y" };
            if (row.Id.EndsWith("_vaapi", StringComparison.Ordinal))
            {
                var node = OperatingSystem.IsLinux() && Directory.Exists("/dev/dri") ? Directory.EnumerateFiles("/dev/dri", "renderD*").Order().FirstOrDefault() : null;
                if (node == null) return "No accessible VAAPI render node.";
                args.AddRange(["-init_hw_device", "vaapi=va:" + node, "-filter_hw_device", "va"]);
            }
            // 720p avoids false negatives from hardware encoders with minimum dimensions.
            args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24", "-frames:v", "6", "-an", "-threads", "2"]);
            if (row.Id.EndsWith("_vaapi", StringComparison.Ordinal)) args.AddRange(["-vf", "format=nv12,hwupload"]);
            args.AddRange(["-c:v", row.Id]);
            args.AddRange(QualityArguments(row, quantizers[i]));
            args.Add(file);
            var result = await Run("ffmpeg", args, ct, TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0) return Failure(result);
            if (Regex.IsMatch(result.StandardError, @"(?i)(has not been used|option.*ignored|selected ratecontrol mode is unsupported|falling back to software)")) return "Requested quality mode was ignored or unsupported: " + Failure(result);
            if (!File.Exists(file) || new FileInfo(file).Length < 1) return "Encoder produced no bitstream.";
            sizes.Add(new FileInfo(file).Length);
            var decode = await Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-threads", "2", "-i", file, "-frames:v", "6", "-f", "null", "-"], ct, TimeSpan.FromSeconds(10));
            if (decode.ExitCode != 0) return "Roundtrip decode failed: " + Failure(decode);
            var probe = await Run("ffprobe", ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,nb_read_frames", "-of", "json", file], ct);
            if (probe.ExitCode != 0) return "Roundtrip probe failed: " + Failure(probe);
            try
            {
                using var json = JsonDocument.Parse(probe.StandardOutput);
                var stream = json.RootElement.GetProperty("streams")[0];
                if (stream.GetProperty("codec_name").GetString() != row.CodecId || stream.GetProperty("width").GetInt32() != 1280 || stream.GetProperty("height").GetInt32() != 720 || stream.GetProperty("nb_read_frames").GetString() != "6") return "Roundtrip codec, dimensions or frame count changed.";
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { return "Invalid roundtrip probe: " + e.Message; }
        }
        if (sizes.Distinct().Count() < 2 || sizes[0] <= sizes[^1]) return "Quality control did not produce the expected size response at three settings.";
        details.AppendLine($"{row.Id}: encode/decode passed at three qualities; sizes {string.Join(",", sizes)}. Source-specific profiles still require validation.");
        return null;
    }

    public static IReadOnlyList<string> QualityArguments(EncoderDescriptor row, double value)
    {
        var q = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (row.Id.EndsWith("_nvenc", StringComparison.Ordinal)) return ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0"];
        if (row.Id.EndsWith("_qsv", StringComparison.Ordinal)) return ["-global_quality", q, "-b:v", "0", "-preset", "medium"];
        if (row.Id.EndsWith("_amf", StringComparison.Ordinal)) return ["-rc", "cqp", "-qp_i", q, "-qp_p", q, "-quality", "quality"];
        if (row.Id.EndsWith("_vaapi", StringComparison.Ordinal)) return ["-rc_mode", "CQP", row.CodecId == CompressionCodecs.Av1 ? "-global_quality" : "-qp", q];
        if (row.Id.EndsWith("_videotoolbox", StringComparison.Ordinal)) return ["-allow_sw", "0", "-realtime", "0", "-q:v", q];
        return row.Id switch
        {
            "libsvtav1" => ["-crf", q, "-preset", "10", "-svtav1-params", "lp=2"],
            "libaom-av1" => ["-crf", q, "-b:v", "0", "-cpu-used", "8", "-row-mt", "1"],
            "libvpx-vp9" => ["-crf", q, "-b:v", "0", "-deadline", "good", "-cpu-used", "5", "-row-mt", "1"],
            _ => ["-crf", q, "-preset", "fast"]
        };
    }

    private async Task<string?> ProbeImage(string encoder, string decoder, string input, string encoded, string[] encodeArgs, string[] decodeArgs, CancellationToken ct)
    {
        if (!File.Exists(input)) return "Reference image could not be generated.";
        var reason = await Attempt(encoder, encodeArgs, ct);
        if (reason != null) return reason;
        if (!File.Exists(encoded) || new FileInfo(encoded).Length == 0) return "Image encoder produced no file.";
        reason = await Attempt(decoder, decodeArgs, ct);
        if (reason != null) return "Decode failed: " + reason;
        var decoded = decodeArgs[^1];
        var check = await Run("magick", ["identify", "-format", "%wx%h", decoded], ct);
        return check.ExitCode == 0 && check.StandardOutput.Trim() == "128x96" ? null : "Image dimensions failed roundtrip verification: " + Failure(check);
    }

    private async Task<bool> ProbeMetric(string name, string[] args, double minimum, double maximum, CancellationToken ct)
    {
        var result = await Run(name, args, ct);
        var first = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        var valid = result.ExitCode == 0 && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) && double.IsFinite(score) && score >= minimum && score <= maximum;
        details.AppendLine($"{name}: {(valid ? "identity score verified" : Failure(result))}");
        return valid;
    }

    private async Task<bool> ProbeVmaf(string work, string model1080, string model4k, CancellationToken ct)
    {
        var reference = Path.Combine(work, "metric-reference.y4m");
        var make = await Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24", "-frames:v", "3", "-pix_fmt", "yuv420p10le", "-strict", "-1", reference], ct);
        if (make.ExitCode != 0) return false;
        // The 4K model load is tested here; this small fixture is a capability check,
        // not a quality evaluation or calibration at its viewing resolution.
        foreach (var model in new[] { model1080, model4k })
        {
            var log = Path.Combine(work, Path.GetFileName(model));
            // Official 3.2.1 embeds these exact models. Its CLI splits model options
            // at every colon, including a Windows drive letter (escaping is not supported).
            var run = await Run("vmaf", ["-r", reference, "-d", reference, "-m", "version=" + Path.GetFileNameWithoutExtension(model) + ":name=pixseek", "--threads", "2", "--output", log, "--json"], ct, TimeSpan.FromSeconds(25));
            if (run.ExitCode != 0 || !File.Exists(log)) { details.AppendLine("VMAF v1 unavailable: " + Failure(run)); return false; }
            try
            {
                using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(log, ct));
                if (!parsed.RootElement.TryGetProperty("frames", out var frames) || frames.GetArrayLength() != 3) return false;
                foreach (var frame in frames.EnumerateArray())
                    if (!frame.GetProperty("metrics").TryGetProperty("pixseek", out var score) || !double.IsFinite(score.GetDouble()) || score.GetDouble() < 99) return false;
            }
            catch (JsonException) { return false; }
        }
        details.AppendLine("Standalone VMAF: both pinned v1 models executed successfully.");
        return true;
    }

    private async Task<ProcessResult> Run(string name, IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        try { return await runner.RunAsync(tools.PathFor(name), args, ct, timeout: timeout ?? TimeSpan.FromSeconds(20)); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { return new(-1, "", e.Message); }
    }
    private async Task<string?> Attempt(string name, string[] args, CancellationToken ct) { var r = await Run(name, args, ct); return r.ExitCode == 0 ? null : Failure(r); }
    private async Task<string> Describe(string name, string[] args, CancellationToken ct)
    {
        var r = await Run(name, args, ct);
        var value = r.StandardOutput + r.StandardError;
        details.AppendLine(name + ": " + (r.ExitCode == 0 ? FirstLine(value) : Failure(r)));
        return value;
    }
    private static string FirstLine(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Unavailable";
    private static string Failure(ProcessResult result)
    {
        var value = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var important = value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => Regex.IsMatch(x, @"(?i)(failed|error|unsupported|cannot|not found|absent|DLL|not supported|no such)")).Take(4);
        var message = string.Join(" ", important);
        if (string.IsNullOrWhiteSpace(message)) message = value;
        return $"exit {result.ExitCode}: " + message[..Math.Min(900, message.Length)].Trim();
    }

    // Cache invalidation uses file metadata only. Never read/hash executable contents.
    private string CacheKey()
    {
        var value = new StringBuilder($"schema={Schema}|{RuntimeInformation.OSDescription}|{RuntimeInformation.ProcessArchitecture}|cpu={Environment.ProcessorCount}");
        foreach (var name in new[] { "ffmpeg", "ffprobe", "magick", "cjxl", "djxl", "avifenc", "avifdec", "heif-enc", "heif-dec", "ssimulacra2", "butteraugli", "vmaf", "exiftool" })
        {
            AppendFileMetadata(value, tools.PathFor(name));
        }
        foreach (var name in new[] { "vmaf_v1.0.16_3d0h.json", "vmaf_v1.0.16_1d5h_2160.json" })
        {
            var model = tools.ModelPath(name);
            if (model != null) AppendFileMetadata(value, model);
        }
        var manifest = Path.Combine(tools.NativeDirectory, "compression-dependencies.json");
        AppendFileMetadata(value, manifest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static void AppendFileMetadata(StringBuilder value, string path)
    {
        var file = new FileInfo(path);
        value.Append('|').Append(file.FullName).Append(':').Append(file.Exists ? file.Length : -1)
            .Append(':').Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
    }

    private async Task<string> DeviceFingerprint(CancellationToken ct)
    {
        var value = new StringBuilder($"cpu={Environment.ProcessorCount}|{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
        if (OperatingSystem.IsWindows())
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var exe = Path.Combine(directory.Trim('"'), "nvidia-smi.exe");
                if (!File.Exists(exe)) continue;
                try { var r = await runner.RunAsync(exe, ["--query-gpu=name,uuid,driver_version", "--format=csv,noheader"], ct, timeout: TimeSpan.FromSeconds(5)); value.Append(r.StandardOutput); } catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception) { value.Append(e.Message); }
                break;
            }
            try
            {
                using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (adapters != null) foreach (var key in adapters.GetSubKeyNames()) { using var adapter = adapters.OpenSubKey(key); value.Append(adapter?.GetValue("DriverDesc")).Append(adapter?.GetValue("DriverVersion")).Append(adapter?.GetValue("MatchingDeviceId")); }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { value.Append(e.Message); }
        }
        if (OperatingSystem.IsLinux() && Directory.Exists("/sys/class/drm"))
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
                foreach (var part in new[] { "device/uevent", "device/driver/module/version" }) { var file = Path.Combine(card, part); try { if (File.Exists(file)) value.Append(await File.ReadAllTextAsync(file, ct)); } catch (IOException) { } }
        if (OperatingSystem.IsMacOS())
            try { var r = await runner.RunAsync("/usr/sbin/system_profiler", ["SPDisplaysDataType", "-json"], ct, timeout: TimeSpan.FromSeconds(10)); value.Append(r.StandardOutput); } catch (Exception e) when (e is IOException or TimeoutException) { value.Append(e.Message); }
        details.AppendLine("Device fingerprint: " + value);
        return value.ToString();
    }

    private static bool PlatformSupports(EncoderDescriptor row)
    {
        if (!row.Hardware) return true;
        if (row.Id.EndsWith("_videotoolbox", StringComparison.Ordinal)) return OperatingSystem.IsMacOS();
        if (row.Id.EndsWith("_vaapi", StringComparison.Ordinal)) return OperatingSystem.IsLinux();
        return !OperatingSystem.IsMacOS();
    }
    private static IEnumerable<EncoderDescriptor> VideoCatalog()
    {
        EncoderDescriptor Make(string id, string codec, string quality, double max, double initial, bool hw = false, string rate = "crf", bool lower = true) => new() { Id = id, CodecId = codec, Tool = "ffmpeg", QualityName = quality, Minimum = hw ? 1 : 0, Maximum = max, DefaultQuality = initial, Hardware = hw, RateControl = rate, LowerIsBetter = lower, PixelFormats = ["yuv420p"] };
        yield return Make("libsvtav1", "av1", "CRF", 63, 28);
        yield return Make("libaom-av1", "av1", "CRF", 63, 28);
        yield return Make("libx265", "hevc", "CRF", 51, 22);
        yield return Make("libx264", "h264", "CRF", 51, 20);
        yield return Make("libvpx-vp9", "vp9", "CRF", 63, 28);
        foreach (var (codec, initial) in new[] { ("av1", 27d), ("hevc", 23d), ("h264", 21d) })
        {
            yield return Make(codec + "_nvenc", codec, "CQ", codec == "av1" ? 63 : 51, initial, true, "cq");
            yield return Make(codec + "_qsv", codec, "ICQ", 51, 23, true, "icq");
            yield return Make(codec + "_amf", codec, codec == "av1" ? "QIndex" : "QP", codec == "av1" ? 255 : 51, codec == "av1" ? 112 : 24, true, "cqp");
            yield return Make(codec + "_vaapi", codec, codec == "av1" ? "QIndex" : "QP", codec == "av1" ? 255 : 51, codec == "av1" ? 112 : 24, true, "cqp");
            if (codec != "av1") yield return Make(codec + "_videotoolbox", codec, "Quality", 100, 65, true, "quality", false);
        }
    }
    private sealed record CapabilityCache(int Schema, string Fingerprint, DateTimeOffset CheckedAt, ToolCapabilities Capabilities);
}
