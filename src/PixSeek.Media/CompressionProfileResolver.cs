using System.Globalization;
using PixSeek.Core;

namespace PixSeek.Media;
public sealed record CompressionPlan(string Extension, string Tool, IReadOnlyList<string> Arguments, bool Lossless, bool Reversible, string? SkipReason = null);
public sealed class CompressionProfileResolver(NativeTools tools)
{
    public CompressionPlan Resolve(EncodingRequest r)
    {
        var o = r.Options;
        var caps = tools.Capabilities ?? throw new InvalidOperationException("Diagnostics des codecs requis.");
        CompressionPlan Skip(string reason) => new("", "", [], false, false, reason);
        if (r.MediaType == MediaType.Image)
        {
            if (o.ImageCodec == "Conserver")
                return Skip("Format conservé sur demande.");
            if (r.Metadata.FrameCount > 1)
                return Skip("Image animée ou multipage conservée intégralement.");
            if (Path.GetExtension(r.SourcePath).ToLowerInvariant()is ".jxl" or ".avif")
                return Skip("Format moderne déjà compressé.");
            var lossless = o.Preset == CompressionPreset.Preserve || o.Lossless;
            var codec = o.ImageCodec == "AVIF" || (o.ImageCodec == "Auto" && !caps.Jxl && caps.Avif && !lossless) ? "AVIF" : "JPEG XL";
            if (lossless && codec == "AVIF")
                return Skip("Le chemin AVIF sans perte n'est pas validé ; choisissez JPEG XL.");
            if (codec == "JPEG XL" && !caps.Jxl)
                return Skip("Encodeur JPEG XL indisponible.");
            if (codec == "AVIF" && !caps.Avif)
                return Skip("Encodeur AVIF indisponible.");
            var jpeg = Path.GetExtension(r.SourcePath).ToLowerInvariant()is ".jpg" or ".jpeg";
            if (lossless && jpeg && o.KeepMetadata && File.Exists(tools.PathFor("cjxl")) && File.Exists(tools.PathFor("djxl")))
                return new(".jxl", "cjxl", [r.SourcePath, r.DestinationPath, "-d", "0", "-e", Math.Clamp(o.Effort, 1, 9).ToString(), "--lossless_jpeg=1"], true, true);
            var args = new List<string>
            {
                r.SourcePath
            };
            if (!o.KeepMetadata)
                args.Add("-strip");
            if (codec == "JPEG XL")
            {
                var distance = lossless ? 0 : o.Distance ?? (o.Preset == CompressionPreset.Maximum ? 3 : 1);
                if (distance is < 0 or > 25)
                    throw new ArgumentOutOfRangeException(nameof(o.Distance));
                args.AddRange(["-define", "jxl:distance=" + distance.ToString(CultureInfo.InvariantCulture), "-define", "jxl:effort=" + Math.Clamp(o.Effort, 1, 9)]);
            }
            else
                args.AddRange(["-quality", Math.Clamp(o.ImageQuality ?? (o.Preset == CompressionPreset.Maximum ? 45 : 75), 1, 100).ToString(CultureInfo.InvariantCulture), "-define", "heic:speed=4"]);
            args.Add((codec == "JPEG XL" ? "JXL:" : "AVIF:") + r.DestinationPath);
            return new(codec == "JPEG XL" ? ".jxl" : ".avif", "magick", args, lossless, false);
        }

        if (o.VideoCodec == "Conserver")
            return Skip("Vidéo conservée sur demande.");
        if (o.VideoCodec is not ("AV1" or "Auto"))
            throw new ArgumentException("Codec vidéo non pris en charge.");
        if (o.Preset == CompressionPreset.Preserve)
            return Skip("Vidéo conservée : aucun réencodage avec perte dans ce mode.");
        if (caps.Av1Encoder == null)
            return Skip("Encodeur AV1 indisponible.");
        if (r.Metadata.Codec == "av1")
            return Skip("Vidéo déjà en AV1.");
        if (r.Metadata.Hdr)
            return Skip("Vidéo HDR conservée : préservation HDR non validée.");
        var crf = o.Crf ?? (o.Preset == CompressionPreset.Maximum ? 38 : 30);
        if (crf is < 0 or > 63)
            throw new ArgumentOutOfRangeException(nameof(o.Crf));
        var video = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-v",
            "error",
            "-n",
            "-i",
            r.SourcePath,
            "-map",
            "0",
            "-map_metadata",
            o.KeepMetadata ? "0" : "-1",
            "-map_chapters",
            "0",
            "-c",
            "copy",
            "-c:v",
            caps.Av1Encoder,
            "-crf",
            crf.ToString(),
            "-pix_fmt",
            "yuv420p10le"
        };
        if (caps.Av1Encoder == "libsvtav1")
            video.AddRange(["-preset", Math.Clamp(o.Speed, 0, 13).ToString(), "-svtav1-params", "lp=4"]);
        else
            video.AddRange(["-cpu-used", Math.Clamp(o.Speed, 0, 8).ToString(), "-b:v", "0", "-row-mt", "1", "-threads", "4"]);
        video.AddRange(["-progress", "pipe:1", "-nostats", "-f", "matroska", r.DestinationPath]);
        return new(".mkv", "ffmpeg", video, false, false);
    }

    public static string DisplayCommand(CompressionPlan p) => p.SkipReason ?? p.Tool + " " + string.Join(" ", p.Arguments.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
}
