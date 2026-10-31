using System.Globalization;
using PixSeek.Core;

namespace PixSeek.Media;

public sealed record CompressionPlan(string Extension, string Tool, IReadOnlyList<string> Arguments, bool Lossless, bool Reversible, string? SkipReason = null)
{
    public string CodecId { get; init; } = "";
    public string EncoderId { get; init; } = "";
    public string RateControl { get; init; } = "";
    public double NativeQuality { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string PixelFormat { get; init; } = "";
    public bool NormalizesOrientation { get; init; }
    public bool ConvertsToSrgb { get; init; }
    public bool IsVideoCopy { get; init; }
    public IReadOnlyList<AudioEncodingPlan> AudioPlans { get; init; } = [];
}

/// <summary>Pure command assembly. Quality search happens once, asynchronously, in the planner.</summary>
public sealed class CompressionProfileResolver(NativeTools tools)
{
    public const string Input = "{pixseek-input}", Output = "{pixseek-output}";
    internal NativeTools Tools => tools;
    public static CompressionPlan Skip(string reason) => new("", "", [], false, false, reason);
    public CompressionPlan Resolve(EncodingRequest r)
    {
        var codec = CompressionCodecs.Normalize(r.MediaType == MediaType.Image ? r.Options.ImageCodec : r.Options.VideoCodec);
        if (codec == "original") return Skip("Keep original : média intégralement conservé sur demande.");
        if (codec == "auto") return Skip("La sélection Auto nécessite la préparation adaptative asynchrone.");
        var id = r.MediaType == MediaType.Image ? r.Options.ImageEncoderId : r.Options.VideoEncoderId;
        var e = tools.Capabilities?.Encoders.FirstOrDefault(e => e.Available && e.CodecId == codec && (id == null || e.Id == id));
        if (e == null) return Skip("Codec ou encodeur demandé indisponible : " + (id ?? codec));
        var q = r.MediaType == MediaType.Image ? r.Options.ImageNativeQuality ?? r.Options.Distance ?? r.Options.ImageQuality ?? e.DefaultQuality
            : r.Options.VideoNativeQuality ?? r.Options.Crf ?? e.DefaultQuality;
        return Bind(Create(r, e, q, r.Metadata.Width, r.Metadata.Height), r.SourcePath, r.DestinationPath);
    }
    public static CompressionPlan Bind(CompressionPlan p, string source, string destination) => p with
    {
        Arguments = p.Arguments.Select(a => a == Input ? source : a == Output ? destination : a).ToArray()
    };
    public CompressionPlan Create(EncodingRequest r, EncoderDescriptor e, double q, int width, int height,
        bool lossless = false, bool reversible = false, bool copyVideo = false)
    {
        if (!double.IsFinite(q) || q < e.Minimum || q > e.Maximum) throw new ArgumentOutOfRangeException(nameof(q), $"{e.Id} : {e.Minimum}–{e.Maximum} {e.QualityName}.");
        var p = new CompressionPlan(Extension(e.CodecId), e.Tool, [], lossless || e.CodecId == "png" || (e.CodecId == "jxl" && q == 0), reversible)
        {
            CodecId = e.CodecId, EncoderId = e.Id, RateControl = e.RateControl, NativeQuality = q, Width = width, Height = height,
            NormalizesOrientation = r.MediaType == MediaType.Image && !reversible,
            ConvertsToSrgb = r.MediaType == MediaType.Image && !r.Options.KeepMetadata && !reversible, IsVideoCopy = copyVideo
        };
        return r.MediaType == MediaType.Image ? Image(r, e, p) : Video(r, e, p);
    }
    public static string Extension(string codec) => codec switch { "jxl" => ".jxl", "avif" => ".avif", "heic" => ".heic", "png" => ".png", _ => ".mkv" };
    private static CompressionPlan Image(EncodingRequest r, EncoderDescriptor e, CompressionPlan p)
    {
        var q = Number(p.NativeQuality);
        IReadOnlyList<string> args = e.Id switch
        {
            "cjxl" => [Input, Output, "-d", p.Lossless ? "0" : q, "-e", Math.Clamp(r.Options.Effort, 1, 10).ToString(), "--lossless_jpeg=" + (p.Reversible ? "1" : "0"), "--container=1"],
            "avifenc" => ["--qcolor", q, "--qalpha", "100", "--speed", "8", "--jobs", Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)).ToString(), "--yuv", "444", "--depth", r.Metadata.Technical.BitDepth <= 8 ? "8" : r.Metadata.Technical.BitDepth <= 10 ? "10" : "12", Input, Output],
            "heif-enc" => ["-q", q, "-o", Output, Input],
            _ when p.CodecId == "png" => [Input, "-define", "png:compression-level=" + q, Output],
            _ => throw new NotSupportedException("Adaptateur image non validé : " + e.Id)
        };
        return p with { Arguments = args };
    }
    public static IReadOnlyList<AudioEncodingPlan> AudioPlans(MediaMetadata metadata) => metadata.Technical.Streams.Where(s => s.CodecType == "audio").Select((s, i) =>
    {
        var rate = (s.Channels, s.ChannelLayout) switch
        {
            (1, "mono" or "") => 64_000, (2, "stereo" or "") => 92_000,
            (6, "5.1" or "5.1(side)") => 256_000, (8, "7.1" or "7.1(wide)" or "7.1(wide-side)") => 320_000, _ => 0
        };
        // Preserve possible object extensions rather than silently flattening the carrier.
        var copy = rate == 0 || s.Codec is "truehd" or "eac3" or "dts" || s.ChannelLayout is "5.1(side)" or "7.1(wide)" or "7.1(wide-side)" ||
            (!string.IsNullOrEmpty(s.SideDataJson) && s.SideDataJson != "[]") || (s.Codec == "opus" && (s.BitRate == null || s.BitRate <= rate));
        return new AudioEncodingPlan(s.Index, i, copy ? s.Codec : "opus", copy ? null : rate, s.Channels, s.ChannelLayout, copy,
            copy ? "Piste déjà adaptée ou disposition/extension conservée." : "Opus VBR, canaux et disposition conservés.");
    }).ToArray();
    private static CompressionPlan Video(EncodingRequest r, EncoderDescriptor e, CompressionPlan p)
    {
        var streams = r.Metadata.Technical.Streams;
        var primaryIndex = streams.FirstOrDefault(s => s.CodecType == "video" && !s.AttachedPicture)?.Index ?? 0;
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-n", "-noautorotate", "-i", Input };
        if (streams.Count > 0) foreach (var stream in streams.OrderBy(s => s.Index)) args.AddRange(["-map", "0:" + stream.Index]);
        else args.AddRange(["-map", "0"]);
        args.AddRange(["-map_metadata", r.Options.KeepMetadata ? "0" : "-1", "-map_chapters", r.Options.KeepMetadata ? "0" : "-1", "-c", "copy"]);
        foreach (var (stream, outputIndex) in streams.OrderBy(s => s.Index).Select((s, i) => (s, i)))
        {
            if (r.Options.KeepMetadata)
            {
                args.AddRange(["-map_metadata:s:" + outputIndex, "0:s:" + stream.Index]);
                foreach (var tag in stream.Tags.Where(t => t.Key.ToLowerInvariant() is not ("encoder" or "duration" or "bps" or "number_of_frames" or "number_of_bytes")))
                    args.AddRange(["-metadata:s:" + outputIndex, tag.Key + "=" + tag.Value]);
            }
            var flags = stream.Disposition.Where(d => d.Value != 0).Select(d => d.Key).ToArray();
            args.AddRange(["-disposition:" + outputIndex, flags.Length == 0 ? "0" : string.Join('+', flags)]);
        }
        var videoOrdinal = streams.Where(s => s.CodecType == "video").OrderBy(s => s.Index).TakeWhile(s => s.Index != primaryIndex).Count();
        var spec = ":v:" + videoOrdinal;
        var pixelFormat = p.IsVideoCopy ? r.Metadata.Technical.PixelFormat : SelectPixelFormat(e, r.Metadata);
        if (!p.IsVideoCopy)
        {
            args.AddRange(["-c" + spec, e.Id, "-pix_fmt" + spec, pixelFormat, "-fps_mode" + spec, "passthrough"]);
            args.AddRange(VideoQualityArguments(e, p.NativeQuality, r.Options.Speed));
            var filters = new List<string>();
            if (p.Width != r.Metadata.Width || p.Height != r.Metadata.Height) filters.Add($"scale={p.Width}:{p.Height}:flags=lanczos");
            if (e.Id.Contains("vaapi", StringComparison.Ordinal)) filters.Add($"format={(r.Metadata.Technical.BitDepth > 8 ? "p010le" : "nv12")},hwupload");
            if (filters.Count > 0) args.AddRange(["-filter" + spec, string.Join(',', filters)]);
            foreach (var (name, value) in new[] { ("color_primaries", r.Metadata.Technical.ColorPrimaries), ("color_trc", r.Metadata.Technical.ColorTransfer), ("colorspace", r.Metadata.Technical.ColorSpace), ("color_range", r.Metadata.Technical.ColorRange) })
                if (!string.IsNullOrWhiteSpace(value) && value is not ("unknown" or "unspecified")) args.AddRange(["-" + name + spec, value]);
        }
        var audio = AudioPlans(r.Metadata);
        foreach (var track in audio.Where(a => !a.Copy))
        {
            var a = ":a:" + track.OutputIndex;
            args.AddRange(["-c" + a, "libopus", "-b" + a, track.TargetBitRate!.Value.ToString(), "-vbr" + a, "on", "-application" + a, "audio", "-compression_level" + a, "10", "-frame_duration" + a, "20", "-ar" + a, "48000", "-mapping_family" + a, track.Channels <= 2 ? "0" : "1"]);
        }
        if (e.Id.Contains("vaapi", StringComparison.Ordinal) && !p.IsVideoCopy) args.InsertRange(0, ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-filter_hw_device", "va"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", "-f", "matroska", Output]);
        return p with { Arguments = args, AudioPlans = audio, PixelFormat = pixelFormat };
    }
    public static string SelectPixelFormat(EncoderDescriptor e, MediaMetadata media)
    {
        var input = media.Technical.PixelFormat;
        if (e.PixelFormats.Contains(input)) return input;
        var depth = media.Technical.BitDepth;
        var chroma = media.Technical.ChromaSubsampling;
        var candidates = e.PixelFormats.Where(p => p is not ("cuda" or "d3d11" or "qsv" or "vaapi" or "vulkan") && PixelDepth(p) >= depth &&
            (chroma is not ("444" or "422") || p.Contains(chroma))).ToArray();
        return candidates.FirstOrDefault(p => p == (depth > 8 ? "p010le" : "nv12"))
            ?? candidates.FirstOrDefault(p => p == (depth > 8 ? "yuv420p10le" : "yuv420p"))
            ?? candidates.FirstOrDefault() ?? throw new NotSupportedException("Profondeur ou chroma source non conservable par " + e.Id);
    }
    private static int PixelDepth(string format) => format.Contains("16") || format.Contains("p016") ? 16 : format.Contains("12") ? 12 : format.Contains("10") || format.Contains("p010") ? 10 : 8;
    public static IReadOnlyList<string> VideoQualityArguments(EncoderDescriptor e, double quality, int speed = 10)
    {
        var q = Number(quality);
        var threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)).ToString();
        if (e.Id.EndsWith("_nvenc", StringComparison.Ordinal)) return ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0", "-multipass", "disabled"];
        if (e.Id.EndsWith("_qsv", StringComparison.Ordinal)) return e.RateControl == "icq" ? ["-global_quality", q, "-b:v", "0"] : ["-q:v", q, "-b:v", "0"];
        if (e.Id.EndsWith("_amf", StringComparison.Ordinal)) return ["-rc", "cqp", "-qp_i", q, "-qp_p", q];
        if (e.Id.EndsWith("_vaapi", StringComparison.Ordinal)) return ["-rc_mode", "CQP", e.CodecId == "av1" ? "-global_quality" : "-qp", q];
        if (e.Id.EndsWith("_videotoolbox", StringComparison.Ordinal)) return ["-allow_sw", "0", "-realtime", "0", "-q:v", q];
        return e.Id switch
        {
            "libsvtav1" => ["-crf", q, "-preset", Math.Clamp(speed, 0, 13).ToString(), "-svtav1-params", "lp=" + threads + ":film-grain=0"],
            "libaom-av1" => ["-crf", q, "-b:v", "0", "-cpu-used", "8", "-row-mt", "1", "-threads", threads],
            "libx264" or "libx265" => ["-crf", q, "-preset", "fast", "-threads", threads],
            "libvpx-vp9" => ["-crf", q, "-b:v", "0", "-deadline", "good", "-cpu-used", "6", "-row-mt", "1", "-threads", threads],
            _ => throw new NotSupportedException("Adaptateur vidéo non validé : " + e.Id)
        };
    }
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    public static string DisplayCommand(CompressionPlan p) => p.SkipReason ?? p.Tool + " " + string.Join(" ", p.Arguments.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
}
