using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Full-reference SDR measurements. Scores are product heuristics; sampling is never a transparency certificate.</summary>
public sealed class PerceptualQualityEvaluator(NativeTools tools, ProcessRunner runner, ImageMagickService images)
{
    public async Task<PerceptualQualityReport> EvaluateImageAsync(string reference, string candidate, MediaMetadata source,
        QualityTargets targets, string workspace, bool lossless, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var decoded = Path.Combine(workspace, "decoded-" + id + ".png");
        var identify = await images.IdentifyAsync(reference, ct);
        await images.RunAsync([candidate, "-auto-orient", "-filter", "Lanczos", "-resize", $"{identify.Width}x{identify.Height}!", "-depth", source.Technical.BitDepth <= 8 ? "8" : "16", decoded], ct);
        if (source.HasAlpha)
        {
            var before = (await images.RunAsync([reference, "-alpha", "extract", "-format", "%#", "info:"], ct)).Trim();
            var after = (await images.RunAsync([decoded, "-alpha", "extract", "-format", "%#", "info:"], ct)).Trim();
            if (before != after) return Failed("Alpha", "Le canal alpha a changé.");
        }
        if (lossless)
        {
            var exact = await images.PixelSignatureAsync(reference, ct) == await images.PixelSignatureAsync(decoded, ct);
            return new() { Metric = "decoded-pixel-signature", Version = "ImageMagick", Score = exact ? 100 : 0, MinimumWindowScore = exact ? 100 : 0, Passed = exact, Details = exact ? "Pixels décodés identiques." : "Les pixels du candidat sans perte ont changé." };
        }
        if (tools.Capabilities?.ImageMetrics != true || (targets.Butteraugli != null && tools.Capabilities.Butteraugli != true))
            return Failed("SSIMULACRA2", "Métrique photo requise indisponible ; aucune substitution silencieuse.");
        var scores = new List<double>();
        var butter = new List<double>();
        foreach (var background in source.HasAlpha ? new[] { "black", "white" } : new[] { "" })
        {
            var refRender = reference;
            var outRender = decoded;
            if (background.Length > 0)
            {
                refRender = Path.Combine(workspace, "reference-" + background + id + ".png");
                outRender = Path.Combine(workspace, "candidate-" + background + id + ".png");
                await images.RunAsync([reference, "-background", background, "-alpha", "remove", "-alpha", "off", refRender], ct);
                await images.RunAsync([decoded, "-background", background, "-alpha", "remove", "-alpha", "off", outRender], ct);
            }
            var score = ParseSingleScore(await runner.CheckedAsync(tools.PathFor("ssimulacra2"), [refRender, outRender], ct, timeout: TimeSpan.FromMinutes(5)));
            scores.Add(score);
            var cropScores = new List<double>();
            foreach (var crop in ImageWindows(identify.Width, identify.Height))
            {
                var r = Path.Combine(workspace, "crop-reference-" + id + ".png");
                var d = Path.Combine(workspace, "crop-candidate-" + id + ".png");
                await images.RunAsync([refRender, "-crop", crop, "+repage", r], ct);
                await images.RunAsync([outRender, "-crop", crop, "+repage", d], ct);
                cropScores.Add(ParseSingleScore(await runner.CheckedAsync(tools.PathFor("ssimulacra2"), [r, d], ct, timeout: TimeSpan.FromMinutes(5))));
            }
            scores.AddRange(cropScores.Select(s => s + targets.ImageScore - targets.ImageWindowScore));
            if (targets.Butteraugli != null)
            {
                var result = await runner.RunAsync(tools.PathFor("butteraugli"), [refRender, outRender], ct, timeout: TimeSpan.FromMinutes(5));
                if (result.ExitCode != 0) throw new InvalidDataException("Butteraugli : " + result.StandardError);
                butter.Add(ParseSingleScore(result.StandardOutput));
            }
        }
        var allPassed = scores.All(s => s >= targets.ImageScore) && (butter.Count == 0 || butter.Max() <= targets.Butteraugli);
        return new()
        {
            Metric = "SSIMULACRA2" + (butter.Count > 0 ? "+Butteraugli" : ""), Version = tools.Capabilities.Fingerprint,
            Passed = allPassed, Score = scores[0], MinimumWindowScore = scores.Skip(1).DefaultIfEmpty(scores[0]).Min() - targets.ImageScore + targets.ImageWindowScore,
            Butteraugli = butter.Count == 0 ? null : butter.Max(), Details = "Image entière et fenêtres natives ; seuils heuristiques versionnés."
        };
    }

    public async Task<PerceptualQualityReport> EvaluateVideoWindowAsync(string reference, string candidate, MediaMetadata source,
        double referenceStart, double candidateStart, double duration, QualityTargets targets, string workspace, CancellationToken ct)
    {
        var caps = tools.Capabilities ?? throw new InvalidOperationException("Diagnostics requis.");
        var model = Math.Min(source.Width, source.Height) <= 1080 ? caps.Vmaf1080Model : caps.Vmaf4kModel;
        if (!caps.VideoMetrics || model == null || !File.Exists(model)) return Failed("VMAF v1", "Moteur ou modèle VMAF v1 requis indisponible.");
        var id = Guid.NewGuid().ToString("N");
        var localModel = Path.Combine(workspace, Path.GetFileName(model));
        if (!File.Exists(localModel)) File.Copy(model, localModel);
        var log = Path.Combine(workspace, "vmaf-" + id + ".json");
        var (width, height) = EvaluationSize(source);
        var refIndex = source.Technical.Streams.FirstOrDefault(s => s.CodecType == "video" && !s.AttachedPicture)?.Index ?? 0;
        var filter = $"[0:{refIndex}]settb=AVTB,setpts=PTS-STARTPTS,scale={width}:{height}:flags=lanczos,format=yuv420p10le,setsar=1[r];" +
            $"[1:v:0]settb=AVTB,setpts=PTS-STARTPTS,scale={width}:{height}:flags=lanczos,format=yuv420p10le,setsar=1[d];" +
            $"[d][r]libvmaf=model=path={Path.GetFileName(localModel)}:log_fmt=json:log_path={Path.GetFileName(log)}:n_threads={Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1))}";
        var ff = await runner.RunAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-noautorotate", "-ss", N(referenceStart), "-t", N(duration), "-i", reference,
            "-noautorotate", "-ss", N(candidateStart), "-t", N(duration), "-i", candidate, "-filter_complex", filter, "-an", "-f", "null", "-"], ct,
            timeout: TimeSpan.FromMinutes(10), workingDirectory: workspace);
        if (ff.ExitCode != 0)
        {
            if (!File.Exists(tools.PathFor("vmaf"))) throw new InvalidOperationException("Le modèle VMAF v1 ne peut pas être exécuté : " + ff.StandardError);
            var r = Path.Combine(workspace, "ref-" + id + ".y4m");
            var d = Path.Combine(workspace, "dist-" + id + ".y4m");
            try
            {
                await ExportY4mAsync(reference, referenceStart, duration, width, height, refIndex, r, ct);
                await ExportY4mAsync(candidate, candidateStart, duration, width, height, null, d, ct);
                await runner.CheckedAsync(tools.PathFor("vmaf"), ["-r", r, "-d", d, "-m", "path=" + Path.GetFileName(localModel) + ":name=vmaf", "--output", log, "--json", "--threads", Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)).ToString()], ct, timeout: TimeSpan.FromMinutes(10), workingDirectory: workspace);
            }
            finally { if (File.Exists(r)) File.Delete(r); if (File.Exists(d)) File.Delete(d); }
        }
        var (mean, p5, count) = ParseVmaf(await File.ReadAllTextAsync(log, ct));
        return new()
        {
            Metric = "VMAF v1", Version = Path.GetFileName(model) + "/" + caps.Fingerprint, Sampled = true,
            Score = mean, MinimumWindowScore = mean, Percentile5 = p5, Passed = count > 0 && mean >= targets.VideoMean && p5 >= targets.VideoPercentile5,
            Windows = [new(referenceStart, duration, mean, p5)], Details = $"Fidélité évaluée sur échantillons ; {count} images mesurées, canevas {width}×{height} SDR 10 bits."
        };
    }

    public async Task<PerceptualQualityReport> EvaluateVideoAsync(string reference, string candidate, MediaMetadata source,
        QualityTargets targets, string workspace, bool final, bool perceptual, CancellationToken ct)
    {
        var reports = new List<PerceptualQualityReport>();
        foreach (var w in AdaptiveCompressionPolicy.SampleWindows(source.DurationMilliseconds / 1000d, final, perceptual))
            reports.Add(await EvaluateVideoWindowAsync(reference, candidate, source, w.StartSeconds, w.StartSeconds, w.DurationSeconds, targets, workspace, ct));
        return Combine(reports);
    }
    private Task<string> ExportY4mAsync(string input, double start, double duration, int width, int height, int? streamIndex, string output, CancellationToken ct) =>
        runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-noautorotate", "-ss", N(start), "-t", N(duration), "-i", input,
            "-map", streamIndex == null ? "0:v:0" : "0:" + streamIndex, "-an", "-vf", $"scale={width}:{height}:flags=lanczos,format=yuv420p10le,setsar=1", "-strict", "-1", "-f", "yuv4mpegpipe", output], ct, timeout: TimeSpan.FromMinutes(5));
    public static PerceptualQualityReport Combine(IReadOnlyList<PerceptualQualityReport> reports) => reports.Count == 0 ? Failed("VMAF v1", "Aucune fenêtre mesurable.") : new()
    {
        Metric = reports[0].Metric, Version = reports[0].Version, Sampled = true, Passed = reports.All(r => r.Passed),
        Score = reports.Average(r => r.Score), MinimumWindowScore = reports.Min(r => r.Score), Percentile5 = reports.Min(r => r.Percentile5 ?? 0),
        Windows = reports.SelectMany(r => r.Windows).ToArray(), Details = "Fidélité évaluée sur échantillons ; chaque fenêtre doit satisfaire moyenne et percentile 5."
    };
    public static (double Mean, double Percentile5, int Count) ParseVmaf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var scores = doc.RootElement.GetProperty("frames").EnumerateArray().Select(frame =>
        {
            var metrics = frame.GetProperty("metrics");
            if (metrics.TryGetProperty("vmaf", out var value)) return value.GetDouble();
            return metrics.EnumerateObject().First(p => p.Name.StartsWith("vmaf", StringComparison.OrdinalIgnoreCase)).Value.GetDouble();
        }).Where(double.IsFinite).Order().ToArray();
        if (scores.Length == 0) throw new InvalidDataException("VMAF n'a produit aucune mesure.");
        return (scores.Average(), scores[Math.Max(0, (int)Math.Ceiling(scores.Length * .05) - 1)], scores.Length);
    }
    public static double ParseSingleScore(string output)
    {
        foreach (var line in output.Split('\n'))
            if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var score) && double.IsFinite(score)) return score;
        var match = Regex.Match(output, @"(?im)^(?:butteraugli(?: distance)?|score)\s*[:=]\s*([-+]?\d+(?:\.\d+)?)");
        if (match.Success) return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        throw new InvalidDataException("La métrique n'a pas produit de score valide.");
    }
    public static IReadOnlyList<string> ImageWindows(int width, int height)
    {
        var w = Math.Min(512, width); var h = Math.Min(512, height);
        return new[] { ((width - w) / 2, (height - h) / 2), ((width - w) / 4, (height - h) / 4), (3 * (width - w) / 4, (height - h) / 4), ((width - w) / 4, 3 * (height - h) / 4), (3 * (width - w) / 4, 3 * (height - h) / 4) }
            .Distinct().Select(p => $"{w}x{h}+{p.Item1}+{p.Item2}").ToArray();
    }
    public static (int Width, int Height) EvaluationSize(MediaMetadata source)
    {
        var edge = Math.Min(source.Width, source.Height) <= 1080 ? 1080 : 2160;
        var ratio = source.Width / (double)Math.Max(1, source.Height);
        if (source.Technical.SampleAspectRatio.Split(':') is [var a, var b] && double.TryParse(a, out var n) && double.TryParse(b, out var d) && d > 0) ratio *= n / d;
        return source.Width >= source.Height ? (Math.Max(2, (int)Math.Round(edge * ratio / 2) * 2), edge) : (edge, Math.Max(2, (int)Math.Round(edge / ratio / 2) * 2));
    }
    private static PerceptualQualityReport Failed(string metric, string details) => new() { Metric = metric, Passed = false, Details = details };
    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
