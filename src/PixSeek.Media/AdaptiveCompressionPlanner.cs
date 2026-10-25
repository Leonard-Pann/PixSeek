using System.Diagnostics;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Bounded independent trials from the source; never a chain of lossy generations.</summary>
public sealed class AdaptiveCompressionPlanner(
    NativeTools tools, ProcessRunner runner, CompressionProfileResolver resolver, ImageMagickService images,
    PhotoMetadataService metadata, PerceptualQualityEvaluator evaluator, EncodingPerformanceEstimator performance)
{
    public async Task<PreparedCompressionPlan> PrepareAsync(EncodingRequest request, string workspace,
        IProgress<EncodingProgress> progress, CancellationToken ct, Func<CancellationToken, Task>? checkpoint = null)
    {
        var codec = CompressionCodecs.Normalize(request.MediaType == MediaType.Image ? request.Options.ImageCodec : request.Options.VideoCodec);
        if (codec == "original") return Skipped(request, "Keep original : aucune transformation, y compris audio et métadonnées.");
        if (UnsupportedReason(request.Metadata, request.MediaType) is { } unsupported) return Skipped(request, unsupported);
        var caps = tools.Capabilities ?? throw new InvalidOperationException("Diagnostics des encodeurs requis.");
        Directory.CreateDirectory(workspace);
        return request.MediaType == MediaType.Image
            ? await PhotoAsync(request, codec, workspace, progress, ct, checkpoint)
            : await VideoAsync(request, codec, workspace, progress, ct, checkpoint);
    }

    public static string? UnsupportedReason(MediaMetadata media, MediaType type)
    {
        if (media.Width <= 0 || media.Height <= 0) return "Dimensions source invalides.";
        if (media.Hdr || media.Technical.HasDynamicHdr || media.Technical.HasGainMap || media.Technical.HasAuxiliaryImages)
            return "HDR, gain map ou données auxiliaires conservés : chaîne complète non validée.";
        if (type == MediaType.Image && media.FrameCount > 1) return "Image animée ou multipage conservée intégralement.";
        if (type == MediaType.Video && media.Technical.Interlaced) return "Vidéo entrelacée conservée : transformation des champs non validée.";
        if (type == MediaType.Video && media.HasAlpha) return "Vidéo avec alpha conservée : encodage alpha non validé.";
        if (type == MediaType.Video && media.Technical.Streams.Count(s => s.CodecType == "video" && !s.AttachedPicture) != 1)
            return "Nombre de flux vidéo principaux non pris en charge par la chaîne de vérification.";
        if (type == MediaType.Video && media.Technical.Streams.Any(s => s.CodecType is not ("video" or "audio" or "subtitle" or "attachment")))
            return "Flux auxiliaire non transportable par le conteneur MKV validé ; original conservé.";
        if (type == MediaType.Video && media.DurationMilliseconds <= 0) return "Durée vidéo inconnue : échantillonnage et synchronisation non vérifiables.";
        return null;
    }

    private async Task<PreparedCompressionPlan> PhotoAsync(EncodingRequest r, string codec, string workspace, IProgress<EncodingProgress> progress,
        CancellationToken ct, Func<CancellationToken, Task>? checkpoint)
    {
        var clock = Stopwatch.StartNew();
        var budget = AdaptiveCompressionPolicy.Budget(r.Metadata, MediaType.Image).TotalSeconds;
        var automatic = codec == "auto";
        var quality = r.Options.ImageAutoQuality;
        var targets = AdaptiveCompressionPolicy.Targets(quality);
        var reasons = new List<string>();
        var candidates = new List<CompressionCandidate>();
        var reference = Path.Combine(workspace, "reference.png");
        var referenceArgs = new List<string> { "-limit", "memory", "256MiB", "-limit", "map", "512MiB", r.SourcePath, "-auto-orient" };
        if (!r.Options.KeepMetadata)
        {
            var srgb = Path.Combine(tools.NativeDirectory, "sRGB.icc");
            if (!string.IsNullOrEmpty(r.Metadata.Technical.IccHash))
            {
                if (!File.Exists(srgb)) return Skipped(r, "Profil de destination sRGB absent : suppression du profil source refusée.");
                referenceArgs.AddRange(["-profile", srgb]);
            }
            else referenceArgs.AddRange(["-colorspace", "sRGB"]);
            referenceArgs.AddRange(["-strip", "-set", "colorspace", "sRGB"]);
        }
        referenceArgs.AddRange(["-depth", r.Metadata.Technical.BitDepth <= 8 ? "8" : "16", reference]);
        await images.RunAsync(referenceArgs, ct);
        var normalized = await images.IdentifyAsync(reference, ct);
        var all = tools.Capabilities!.Encoders.Where(e => e.Available && e.CodecId is "jxl" or "avif" or "heic" or "png").ToArray();
        var snapshot = await metadata.ReadAsync(r.SourcePath, ct);
        var selected = automatic ? all.Where(e => e.CodecId is "jxl" or "avif").OrderBy(e => e.CodecId == "jxl" ? 0 : 1).ToArray()
            : all.Where(e => e.CodecId == codec && (r.Options.ImageEncoderId == null || e.Id == r.Options.ImageEncoderId)).Take(1).ToArray();
        if (selected.Length == 0 && automatic) selected = all.Where(e => e.CodecId == "png").Take(1).ToArray();
        if (selected.Length == 0) return Skipped(r, "Encodeur photo demandé indisponible : " + (r.Options.ImageEncoderId ?? codec));
        var attempts = 0;

        async Task<CompressionCandidate?> Trial(EncoderDescriptor encoder, double q, bool reversible = false, bool exact = false,
            int? width = null, int? height = null, string? inputOverride = null)
        {
            if (attempts >= 4) return null;
            await Checkpoint(checkpoint, ct);
            if (r.Options.KeepMetadata && !reversible && PhotoMetadataService.PreservationIssue(snapshot, encoder.CodecId) is { } issue) { reasons.Add(issue); return null; }
            if (r.Metadata.Technical.BitDepth > 12 && encoder.CodecId == "avif") { reasons.Add("AVIF 16 bits non qualifié ; profondeur source conservée."); return null; }
            if (r.Metadata.HasAlpha && encoder.CodecId == "heic") { reasons.Add("Alpha HEIC non qualifié."); return null; }
            var output = Path.Combine(workspace, "photo-" + attempts++ + CompressionProfileResolver.Extension(encoder.CodecId));
            var prepared = resolver.Create(r with { Options = r.Options with { Effort = 5 } }, encoder, q,
                width ?? (reversible ? r.Metadata.Width : normalized.Width), height ?? (reversible ? r.Metadata.Height : normalized.Height), exact, reversible);
            var start = Stopwatch.StartNew();
            progress.Report(new(.06 + attempts * .07, $"Essai {encoder.CodecId} · {encoder.QualityName} {CompressionProfileResolver.Number(q)}") { Phase = "Search", EstimatedRemaining = TimeSpan.FromSeconds(Math.Max(0, budget - clock.Elapsed.TotalSeconds)) });
            try
            {
                var input = reversible ? r.SourcePath : inputOverride ?? reference;
                await RunAsync(prepared, input, output, ct);
                await metadata.ApplyAsync(r with { DestinationPath = output }, prepared, ct);
                PerceptualQualityReport? report = null;
                if (automatic)
                {
                    if (reversible) report = new() { Metric = "JPEG reconstruction", Score = 100, MinimumWindowScore = 100, Passed = true, Details = "Reconstruction exacte à vérifier avant publication." };
                    else report = await evaluator.EvaluateImageAsync(reference, output, r.Metadata, targets, workspace, prepared.Lossless, ct);
                }
                if (report?.Passed == false) { reasons.Add(report.Details); return new(new() { Command = prepared, IsAutomatic = true, SourceMetadata = r.Metadata, QualityReport = report, ReferencePath = reference }, new FileInfo(output).Length, start.Elapsed.TotalSeconds); }
                var candidate = new CompressionCandidate(new()
                {
                    Command = prepared, IsAutomatic = automatic, SourceMetadata = r.Metadata, QualityReport = report,
                    CandidatePath = output, ReferencePath = reference, EstimatedSeconds = clock.Elapsed.TotalSeconds,
                    Reason = reversible ? "JPEG recompressé avec reconstruction exacte." : automatic ? "Plus petit candidat mesuré répondant aux objectifs de fidélité et de temps." : "Codec, backend et qualité native imposés."
                }, new FileInfo(output).Length, clock.Elapsed.TotalSeconds);
                performance.Observe(encoder, r.Metadata, MediaType.Image, prepared.Width, prepared.Height, tools.Capabilities!.Fingerprint, start.Elapsed.TotalSeconds, 0);
                candidates.Add(candidate);
                return candidate;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            { reasons.Add(encoder.Id + " : " + e.Message); return null; }
        }

        if (!automatic)
        {
            var e = selected[0];
            if (r.Options.ImageRateControl is { } mode && mode != e.RateControl) return Skipped(r, "Le contrôle de qualité enregistré ne correspond pas au backend ; reconfigurez le réglage natif.");
            var q = r.Options.ImageNativeQuality ?? (e.CodecId == "jxl" ? r.Options.Distance : r.Options.ImageQuality) ?? e.DefaultQuality;
            var manual = await Trial(e, q, exact: e.CodecId == "png" || (e.CodecId == "jxl" && q == 0));
            return manual?.Plan ?? Skipped(r, string.Join(" ; ", reasons));
        }

        var jxl = selected.FirstOrDefault(e => e.CodecId == "jxl");
        var jpeg = r.Metadata.Codec.Equals("JPEG", StringComparison.OrdinalIgnoreCase) || r.Metadata.Codec.Equals("JPG", StringComparison.OrdinalIgnoreCase);
        if (jxl != null && jpeg && r.Options.KeepMetadata) await Trial(jxl, 0, reversible: true, exact: true);
        if (jxl != null && !jpeg && (r.Metadata.Codec is "PNG" or "BMP" or "TIFF" || r.Metadata.HasAlpha)) await Trial(jxl, 0, exact: true);
        foreach (var encoder in selected)
        {
            if (attempts >= 4 || (candidates.Count > 0 && clock.Elapsed.TotalSeconds >= budget)) break;
            if (!tools.Capabilities.ImageMetrics || (targets.Butteraugli != null && !tools.Capabilities.Butteraugli)) { reasons.Add("Métrique perceptuelle photo requise indisponible ; chemins exacts uniquement."); continue; }
            var q = AdaptiveCompressionPolicy.Seed(encoder, quality);
            var trial = await Trial(encoder, q);
            if (attempts < 4 && clock.Elapsed.TotalSeconds < budget && trial?.Plan.QualityReport?.Passed == false)
                await Trial(encoder, AdaptiveCompressionPolicy.Improve(encoder, q));
        }
        if (candidates.Count == 0)
        {
            var exactEncoder = jxl ?? all.FirstOrDefault(e => e.CodecId == "png");
            if (exactEncoder != null && attempts < 4) await Trial(exactEncoder, exactEncoder.CodecId == "jxl" ? 0 : 6, exact: true);
        }
        var native = CompressionCandidateSelection.Select(candidates, budget);
        if (native != null && (int)r.Options.Preset == 2 && attempts < 4 && clock.Elapsed.TotalSeconds < budget &&
            r.Metadata.Width * (long)r.Metadata.Height >= 8_000_000 && !r.Metadata.HasAlpha && jpeg && tools.Capabilities.ImageMetrics)
        {
            var scaled = Path.Combine(workspace, "reference-smaller.png");
            var width = Math.Max(1, (int)Math.Round(normalized.Width * .8)); var height = Math.Max(1, (int)Math.Round(normalized.Height * .8));
            await images.RunAsync([reference, "-filter", "Lanczos", "-resize", $"{width}x{height}!", scaled], ct);
            var precheck = await evaluator.EvaluateImageAsync(reference, scaled, r.Metadata, targets with { ImageScore = Math.Max(77, targets.ImageScore + 2), Butteraugli = null }, workspace, false, ct);
            if (precheck.Passed)
            {
                var encoder = all.First(e => e.Id == native.Plan.EncoderId);
                var smaller = await Trial(encoder, native.Plan.Quality, width: width, height: height, inputOverride: scaled);
                if (smaller != null && smaller.Plan.QualityReport?.Passed == true && smaller.EstimatedBytes > native.EstimatedBytes * .85) candidates.Remove(smaller);
            }
        }
        var chosen = CompressionCandidateSelection.Select(candidates, budget);
        return chosen?.Plan ?? Skipped(r, reasons.Count > 0 ? string.Join(" ; ", reasons.Distinct()) : "Aucun candidat photo ne satisfait les vérifications.");
    }

    private async Task<PreparedCompressionPlan> VideoAsync(EncodingRequest r, string codec, string workspace, IProgress<EncodingProgress> progress,
        CancellationToken ct, Func<CancellationToken, Task>? checkpoint)
    {
        var clock = Stopwatch.StartNew();
        var automatic = codec == "auto";
        var budget = AdaptiveCompressionPolicy.Budget(r.Metadata, MediaType.Video).TotalSeconds;
        var searchBudget = Math.Max(2, budget * .1);
        var caps = tools.Capabilities!;
        var targets = AdaptiveCompressionPolicy.Targets(r.Options.VideoAutoQuality);
        var reasons = new List<string>();
        var all = caps.Encoders.Where(e => e.Available && e.Tool == "ffmpeg" && e.CodecId is "av1" or "hevc" or "h264" or "vp9").ToArray();
        var selected = automatic ? all.Where(e => Compatible(e, r.Metadata)).OrderBy(e => performance.Estimate(e, r.Metadata, MediaType.Video, r.Metadata.Width, r.Metadata.Height, caps.Fingerprint) <= budget ? 0 : 1)
            .ThenBy(e => performance.Estimate(e, r.Metadata, MediaType.Video, r.Metadata.Width, r.Metadata.Height, caps.Fingerprint) <= budget ? Priority(e, r.Metadata) : 100 + performance.Estimate(e, r.Metadata, MediaType.Video, r.Metadata.Width, r.Metadata.Height, caps.Fingerprint))
            .GroupBy(e => e.CodecId).Select(g => g.First()).Take(2).ToArray()
            : all.Where(e => e.CodecId == codec && (r.Options.VideoEncoderId == null || e.Id == r.Options.VideoEncoderId)).OrderByDescending(e => e.Hardware).Take(1).ToArray();
        selected = selected.Where(e => Compatible(e, r.Metadata)).ToArray();
        if (!automatic)
        {
            if (selected.Length == 0) return Skipped(r, "Codec/backend demandé absent ou incompatible avec la profondeur et la chroma source.");
            var e = selected[0];
            if (r.Options.VideoRateControl is { } mode && mode != e.RateControl) return Skipped(r, "Le contrôle natif enregistré ne correspond pas au backend ; aucune conversion numérique implicite.");
            var command = resolver.Create(r, e, r.Options.VideoNativeQuality ?? r.Options.Crf ?? e.DefaultQuality, r.Metadata.Width, r.Metadata.Height);
            return new() { Command = command, IsAutomatic = false, SourceMetadata = r.Metadata, Reason = "Codec, backend et qualité native imposés ; définition native.", EstimatedSeconds = performance.Estimate(e, r.Metadata, MediaType.Video, command.Width, command.Height, caps.Fingerprint) };
        }
        var candidates = new List<CompressionCandidate>();
        var attempts = 0;
        var windows = AdaptiveCompressionPolicy.SampleWindows(r.Metadata.DurationMilliseconds / 1000d);
        async Task<CompressionCandidate?> Trial(EncoderDescriptor encoder, double quality, int width, int height, bool copy = false)
        {
            if (attempts >= 6) return null;
            await Checkpoint(checkpoint, ct);
            var number = attempts++;
            var command = resolver.Create(r, encoder, quality, width, height, copyVideo: copy);
            var reports = new List<PerceptualQualityReport>();
            double encodingSeconds = 0, metricSeconds = 0, measuredDuration = 0; long bytes = 0;
            progress.Report(new(.08 + attempts * .045, $"Essai {encoder.Id} · {width}×{height} · {encoder.QualityName} {CompressionProfileResolver.Number(quality)}") { Phase = "Search" });
            try
            {
                for (var i = 0; i < windows.Count; i++)
                {
                    await Checkpoint(checkpoint, ct);
                    var window = windows[i];
                    var pre = Math.Min(1, window.StartSeconds);
                    var output = Path.Combine(workspace, $"video-{number}-{i}.mkv");
                    var args = CompressionProfileResolver.Bind(command, r.SourcePath, output).Arguments.ToList();
                    args.InsertRange(args.IndexOf("-i"), ["-ss", CompressionProfileResolver.Number(window.StartSeconds - pre)]);
                    args.InsertRange(args.Count - 1, ["-t", CompressionProfileResolver.Number(window.DurationSeconds + pre)]);
                    var encodingClock = Stopwatch.StartNew();
                    await runner.CheckedAsync(tools.PathFor("ffmpeg"), args, ct);
                    encodingSeconds += encodingClock.Elapsed.TotalSeconds;
                    bytes += new FileInfo(output).Length; measuredDuration += window.DurationSeconds + pre;
                    if (!copy)
                    {
                        var metricClock = Stopwatch.StartNew();
                        reports.Add(await evaluator.EvaluateVideoWindowAsync(r.SourcePath, output, r.Metadata, window.StartSeconds, pre, window.DurationSeconds, targets, workspace, ct));
                        metricSeconds += metricClock.Elapsed.TotalSeconds;
                    }
                    File.Delete(output);
                }
                var report = copy ? new PerceptualQualityReport { Metric = "stream-copy", Score = 100, MinimumWindowScore = 100, Passed = true, Sampled = false, Details = "Flux vidéo copié ; intégrité du bitstream vérifiée avant publication." } : PerceptualQualityEvaluator.Combine(reports);
                var totalEstimate = encodingSeconds / Math.Max(.01, measuredDuration) * r.Metadata.DurationMilliseconds / 1000d +
                    metricSeconds / Math.Max(1, windows.Count) * AdaptiveCompressionPolicy.SampleWindows(r.Metadata.DurationMilliseconds / 1000d, true, (int)r.Options.Preset == 0).Count + clock.Elapsed.TotalSeconds;
                var candidate = new CompressionCandidate(new()
                {
                    Command = command, IsAutomatic = true, SourceMetadata = r.Metadata, QualityReport = report, EstimatedSeconds = totalEstimate,
                    Reason = copy ? "Flux vidéo conservé ; audio optimisé si pertinent." : "Codec/backend sélectionné sur taille, fidélité échantillonnée et temps mesurés."
                }, (long)(bytes / Math.Max(.01, measuredDuration) * r.Metadata.DurationMilliseconds / 1000d), totalEstimate);
                performance.Observe(encoder, r.Metadata, MediaType.Video, width, height, caps.Fingerprint, encodingSeconds, measuredDuration);
                if (report.Passed) candidates.Add(candidate);
                return candidate;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            { reasons.Add(encoder.Id + " : " + e.Message); return null; }
        }
        if (caps.VideoMetrics)
        {
            foreach (var encoder in selected)
            {
                if (attempts >= 6 || (attempts > 0 && clock.Elapsed.TotalSeconds >= searchBudget)) break;
                var q = AdaptiveCompressionPolicy.Seed(encoder, r.Options.VideoAutoQuality);
                var trial = await Trial(encoder, q, r.Metadata.Width, r.Metadata.Height);
                if (attempts < 6 && clock.Elapsed.TotalSeconds < searchBudget && trial != null)
                    await Trial(encoder, trial.Plan.QualityReport!.Passed ? AdaptiveCompressionPolicy.CompressMore(encoder, q) : AdaptiveCompressionPolicy.Improve(encoder, q), r.Metadata.Width, r.Metadata.Height);
            }
        }
        else reasons.Add("VMAF v1 indisponible ; aucun réencodage vidéo automatique avec pertes.");

        // A copy candidate can save space through Opus alone and is also safe when perceptual models are unavailable.
        if (attempts < 6 && (candidates.Count == 0 || clock.Elapsed.TotalSeconds < searchBudget || r.Metadata.Codec == "av1"))
        {
            var copyEncoder = new EncoderDescriptor { Id = "copy", CodecId = r.Metadata.Codec, Tool = "ffmpeg", Minimum = 0, Maximum = 100, RateControl = "copy", Available = true, Hardware = true };
            await Trial(copyEncoder, 0, r.Metadata.Width, r.Metadata.Height, true);
        }
        var native = CompressionCandidateSelection.Select(candidates, budget);
        if (native != null && !native.Plan.IsVideoCopy && (int)r.Options.Preset == 2 && attempts < 6 && clock.Elapsed.TotalSeconds < searchBudget)
        {
            var encoder = all.First(e => e.Id == native.Plan.EncoderId);
            foreach (var (w, h) in AdaptiveCompressionPolicy.SmallerVideoSizes(r.Metadata.Width, r.Metadata.Height))
            {
                if (attempts >= 6 || clock.Elapsed.TotalSeconds >= searchBudget) break;
                var smaller = await Trial(encoder, native.Plan.Quality, w, h);
                if (smaller?.Plan.QualityReport?.Passed == true && smaller.EstimatedBytes > native.EstimatedBytes * .9) candidates.Remove(smaller);
            }
        }
        return CompressionCandidateSelection.Select(candidates, budget)?.Plan ?? Skipped(r, reasons.Count > 0 ? string.Join(" ; ", reasons.Distinct()) : "Aucun candidat vidéo ne satisfait les objectifs.");
    }
    private static int Priority(EncoderDescriptor encoder, MediaMetadata source) => (encoder.CodecId switch { "av1" => 0, "hevc" => 10, "h264" => 20, _ => 30 }) + (encoder.Hardware ? 0 : 5);
    private static bool Compatible(EncoderDescriptor encoder, MediaMetadata media)
    {
        try { _ = CompressionProfileResolver.SelectPixelFormat(encoder, media); return true; }
        catch (NotSupportedException) { return false; }
    }
    private Task<string> RunAsync(CompressionPlan plan, string input, string output, CancellationToken ct) => runner.CheckedAsync(tools.PathFor(plan.Tool), CompressionProfileResolver.Bind(plan, input, output).Arguments, ct);
    private static PreparedCompressionPlan Skipped(EncodingRequest request, string reason) => new() { Command = CompressionProfileResolver.Skip(reason), SourceMetadata = request.Metadata, IsAutomatic = CompressionCodecs.Normalize(request.MediaType == MediaType.Image ? request.Options.ImageCodec : request.Options.VideoCodec) == "auto", Reason = reason };
    private static Task Checkpoint(Func<CancellationToken, Task>? checkpoint, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return checkpoint?.Invoke(ct) ?? Task.CompletedTask; }
}
