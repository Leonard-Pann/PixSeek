using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Transactional publication around a single immutable adaptive decision.</summary>
public sealed class MediaEncoder : IMediaEncoder
{
    private static readonly SemaphoreSlim VideoGate = new(1, 1);
    private readonly NativeTools _tools;
    private readonly ProcessRunner _runner;
    private readonly CompressionVerificationService _verification;
    private readonly AdaptiveCompressionPlanner _planner;
    private readonly MediaAnalyzer _analyzer;
    private readonly EncodingPerformanceEstimator _performance;
    private readonly PerceptualQualityEvaluator _evaluator;
    private readonly PhotoMetadataService _metadata;
    public MediaEncoder(NativeTools tools, ProcessRunner runner, CompressionProfileResolver resolver, CompressionVerificationService verification,
        AdaptiveCompressionPlanner? planner = null, MediaAnalyzer? analyzer = null, EncodingPerformanceEstimator? performance = null,
        PerceptualQualityEvaluator? evaluator = null, PhotoMetadataService? metadata = null)
    {
        _tools = tools; _runner = runner; _verification = verification;
        var images = new ImageMagickService(tools, runner);
        _metadata = metadata ?? new(tools, runner, images);
        _performance = performance ?? new(Path.Combine(tools.CacheDirectory, "encoding-performance-v2.json"));
        _evaluator = evaluator ?? new(tools, runner, images);
        _analyzer = analyzer ?? new(images, new FfprobeService(tools, runner), _metadata);
        _planner = planner ?? new(tools, runner, resolver, images, _metadata, _evaluator, _performance);
    }
    public string Name => "Adaptive ImageMagick / libjxl / libavif / libheif / FFmpeg";
    public string CapabilitiesFingerprint => _tools.Capabilities?.Fingerprint ?? "";
    public Task<EncodingResult> EncodeAsync(EncodingRequest request, IProgress<EncodingProgress> progress, CancellationToken cancellationToken) =>
        EncodeAsync(request, progress, cancellationToken, null);

    public async Task<EncodingResult> EncodeAsync(EncodingRequest request, IProgress<EncodingProgress> progress, CancellationToken ct, Func<CancellationToken, Task>? checkpoint)
    {
        var clock = Stopwatch.StartNew();
        var source = new FileInfo(request.SourcePath);
        var original = source.Length; var modified = source.LastWriteTimeUtc;
        var sourceFingerprint = Fingerprint(source.FullName);
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))!;
        string? workspace = null, temp = null;
        PreparedCompressionPlan? prepared = null;
        var hasVideoGate = false;
        try
        {
            if (CompressionCodecs.Normalize(request.MediaType == MediaType.Image ? request.Options.ImageCodec : request.Options.VideoCodec) == "original")
                return new(request.SourcePath, null, original, original, "Ignoré", "Keep original : aucune transformation, y compris audio et métadonnées.");
            if (request.MediaType == MediaType.Video) { await VideoGate.WaitAsync(ct); hasVideoGate = true; }
            if (_tools.Capabilities == null) await _tools.DetectAsync(ct);
            progress.Report(new(.01, "Analyse du média et des capacités") { Phase = "Analysis" });
            var actualMetadata = await _analyzer.AnalyzeAsync(request.SourcePath, request.MediaType, ct);
            request = request with { Metadata = actualMetadata };
            Directory.CreateDirectory(destinationDirectory);
            workspace = Path.Combine(destinationDirectory, ".pixseek-compression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            prepared = await _planner.PrepareAsync(request, workspace, progress, ct, checkpoint);
            if (prepared.SkipReason != null) return WithDecision(new(request.SourcePath, null, original, original, "Ignoré", prepared.SkipReason), prepared);
            var final = request.DestinationIsStem ? request.DestinationPath + prepared.Command.Extension : Path.ChangeExtension(request.DestinationPath, prepared.Command.Extension);
            if (string.Equals(Path.GetFullPath(final), source.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return WithDecision(new(request.SourcePath, null, original, original, "Ignoré", "Une sortie ne peut pas remplacer son original."), prepared);
            if (File.Exists(final)) return WithDecision(new(request.SourcePath, null, original, original, "Ignoré", "Sortie existante conservée ; aucun écrasement."), prepared);
            temp = Path.Combine(destinationDirectory, ".pixseek-" + Guid.NewGuid().ToString("N") + ".partial" + prepared.Command.Extension);
            var estimate = Math.Max(0, prepared.EstimatedSeconds - clock.Elapsed.TotalSeconds);
            progress.Report(new(.35, prepared.Reason) { Phase = "Encoding", EstimatedRemaining = TimeSpan.FromSeconds(estimate) });
            if (prepared.CandidatePath != null)
                File.Move(prepared.CandidatePath, temp, false); // Photo trials are complete files, not material to encode a second time.
            else
            {
                var command = CompressionProfileResolver.Bind(prepared.Command, prepared.ReferencePath ?? request.SourcePath, temp);
                var encodingClock = Stopwatch.StartNew();
                await _runner.CheckedAsync(_tools.PathFor(command.Tool), command.Arguments, ct, line =>
                {
                    if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)) return;
                    var fraction = Math.Clamp(microseconds / Math.Max(1, request.Metadata.DurationMilliseconds * 1000d), 0, 1);
                    var remaining = fraction > .01 ? encodingClock.Elapsed.TotalSeconds * (1 - fraction) / fraction : estimate;
                    progress.Report(new(.35 + fraction * .45, $"{prepared.CodecId} · {prepared.EncoderId}") { Phase = "Encoding", EstimatedRemaining = TimeSpan.FromSeconds(Math.Clamp(remaining, 0, TimeSpan.FromDays(365).TotalSeconds)) });
                });
                if (request.MediaType == MediaType.Image) await _metadata.ApplyAsync(request with { DestinationPath = temp }, prepared.Command, ct);
            }
            await (checkpoint?.Invoke(ct) ?? Task.CompletedTask);
            progress.Report(new(.82, "Décodage et vérification d’intégrité") { Phase = "Verification" });
            var verified = await _verification.VerifyAsync(request with { DestinationPath = temp }, prepared, ct);
            if (prepared.IsAutomatic && request.MediaType == MediaType.Video && !prepared.IsVideoCopy)
            {
                progress.Report(new(.9, "Fidélité évaluée sur échantillons") { Phase = "Quality" });
                var report = await _evaluator.EvaluateVideoAsync(request.SourcePath, temp, request.Metadata,
                    AdaptiveCompressionPolicy.Targets(request.Options.VideoAutoQuality), workspace, true, (int)request.Options.Preset == 0, ct);
                prepared = prepared with { QualityReport = report };
                if (!report.Passed) return WithDecision(new(request.SourcePath, null, original, original, "Ignoré", "Contrôle perceptuel final insuffisant ; original conservé. " + report.Details), prepared);
                verified += " Fidélité évaluée sur échantillons (VMAF v1).";
            }
            source.Refresh();
            if (!source.Exists || source.Length != original || source.LastWriteTimeUtc != modified)
                throw new IOException("Source modifiée pendant la conversion ; résultat non validé.");
            var size = new FileInfo(temp).Length;
            if (request.Options.KeepOnlySmaller && size >= original)
                return WithDecision(new(request.SourcePath, null, original, size, "Aucun gain", verified), prepared);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, final, false);
            try { File.SetLastWriteTimeUtc(final, modified); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            progress.Report(new(1, verified) { Phase = "Completed", EstimatedRemaining = TimeSpan.Zero });
            return WithDecision(new(request.SourcePath, final, original, size, "Converti", verified), prepared);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            var result = new EncodingResult(request.SourcePath, null, original, original, "Échec", "Non validé", e.Message);
            return prepared == null ? result : WithDecision(result, prepared);
        }
        finally
        {
            if (temp != null && File.Exists(temp)) File.Delete(temp);
            if (workspace != null && Directory.Exists(workspace))
            {
                // Workspace is an exact, freshly-created directory owned by this operation.
                try { Directory.Delete(workspace, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (hasVideoGate) VideoGate.Release();
            await _performance.SaveAsync(CancellationToken.None);
        }

        EncodingResult WithDecision(EncodingResult result, PreparedCompressionPlan plan) => result with
        {
            Decision = new()
            {
                AlgorithmVersion = AdaptiveCompressionPolicy.Version, CodecId = plan.CodecId, SourceCodecId = request.Metadata.Codec, EncoderId = plan.EncoderId,
                RateControl = plan.Command.RateControl, NativeQuality = plan.Quality, Width = plan.Width, Height = plan.Height,
                Automatic = plan.IsAutomatic, VideoCopied = plan.IsVideoCopy, Reason = plan.Reason, SourceFingerprint = sourceFingerprint,
                CapabilitiesFingerprint = CapabilitiesFingerprint, ElapsedSeconds = clock.Elapsed.TotalSeconds, EstimatedSeconds = plan.EstimatedSeconds, Quality = plan.QualityReport
            }
        };
    }
    public static string Fingerprint(string file)
    {
        var info = new FileInfo(file);
        return "stat-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
    }
}
