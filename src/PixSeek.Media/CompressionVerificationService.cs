using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PixSeek.Core;

namespace PixSeek.Media;

public sealed class CompressionVerificationService(ImageMagickService images, FfprobeService probe, FfmpegService ffmpeg, NativeTools tools, ProcessRunner runner)
{
    private readonly PhotoMetadataService metadata = new(tools, runner, images);
    public Task<string> VerifyAsync(EncodingRequest request, CompressionPlan plan, CancellationToken ct) => VerifyCoreAsync(request, plan, null, ct);
    public Task<string> VerifyAsync(EncodingRequest request, PreparedCompressionPlan plan, CancellationToken ct) =>
        VerifyCoreAsync(request with { Metadata = plan.SourceMetadata }, plan.Command, plan, ct);

    private async Task<string> VerifyCoreAsync(EncodingRequest request, CompressionPlan plan, PreparedCompressionPlan? prepared, CancellationToken ct)
    {
        if (!File.Exists(request.DestinationPath) || new FileInfo(request.DestinationPath).Length == 0) throw new InvalidDataException("Sortie vide ou absente.");
        if (prepared?.QualityReport is { Passed: false }) throw new InvalidDataException("La cible de fidélité perceptuelle n'est pas atteinte.");
        if (prepared is { IsAutomatic: true, QualityReport: null } && !plan.Lossless && !plan.Reversible && !plan.IsVideoCopy)
            throw new InvalidDataException("Aucune mesure de fidélité ne valide cet encodage automatique.");
        if (request.MediaType == MediaType.Video)
        {
            var output = await probe.ProbeAsync(request.DestinationPath, ct);
            ValidateVideoMetadata(request.Metadata, output, plan, request.Options.KeepMetadata);
            if (request.Options.KeepMetadata) await new ContainerMetadataGuard(tools, runner).VerifyAsync(request.SourcePath, request.DestinationPath, ct);
            var sourceStreams = request.Metadata.Technical.Streams.OrderBy(s => s.Index).ToArray();
            var outputStreams = output.Technical.Streams.OrderBy(s => s.Index).ToArray();
            var copied = sourceStreams.Select((stream, i) => (stream, i)).Where(pair =>
                pair.stream.CodecType == "subtitle" || pair.stream.AttachedPicture ||
                (pair.stream.CodecType == "audio" && plan.AudioPlans.Any(a => a.InputIndex == pair.stream.Index && a.Copy))).ToArray();
            if (copied.Length > 0)
            {
                var beforePackets = await CopiedPacketHashesAsync(request.SourcePath, copied.Select(p => p.stream.Index), ct);
                var afterPackets = await CopiedPacketHashesAsync(request.DestinationPath, copied.Select(p => outputStreams[p.i].Index), ct);
                if (!beforePackets.SequenceEqual(afterPackets, StringComparer.Ordinal))
                    throw new InvalidDataException("Une piste audio, un sous-titre ou une pochette annoncés comme copiés ont été modifiés.");
            }
            if (plan.IsVideoCopy)
            {
                var sourceHash = await VideoPacketHashesAsync(request.SourcePath, ct);
                var outputHash = await VideoPacketHashesAsync(request.DestinationPath, ct);
                if (!sourceHash.SequenceEqual(outputHash, StringComparer.Ordinal))
                    throw new InvalidDataException("Le flux vidéo annoncé comme copié diffère des paquets source.");
            }
            var decoded = await ffmpeg.VerifyDecodeStatisticsAsync(request.DestinationPath, ct);
            if (request.Metadata.FrameCount > 1 && !request.Metadata.Technical.Streams.Any(s => s.AttachedPicture) && decoded.Frames != request.Metadata.FrameCount)
                throw new InvalidDataException($"Nombre d'images vidéo différent : {decoded.Frames} au lieu de {request.Metadata.FrameCount}.");
            return $"{output.Codec.ToUpperInvariant()} décodé intégralement ; dimensions prévues, durée, colorimétrie, pistes et synchronisation contrôlées. " + QualityDescription(prepared, plan);
        }
        var result = await images.IdentifyAsync(request.DestinationPath, ct);
        ValidatePhotoMetadata(request.Metadata, result, plan);
        _ = await images.PixelSignatureAsync(request.DestinationPath, ct); // Force a full raster decode, beyond identify -ping.
        if (plan.Reversible)
        {
            if (!request.Options.KeepMetadata) throw new InvalidDataException("Une reconstruction JPEG réversible conserverait les métadonnées supprimées sur demande.");
            var reconstructed = request.DestinationPath + ".reconstructed.jpg";
            try
            {
                await runner.CheckedAsync(tools.PathFor("djxl"), [request.DestinationPath, reconstructed], ct);
                await using var src = File.OpenRead(request.SourcePath);
                await using var dst = File.OpenRead(reconstructed);
                var sourceHash = await SHA256.HashDataAsync(src, ct);
                var outputHash = await SHA256.HashDataAsync(dst, ct);
                if (!sourceHash.SequenceEqual(outputHash))
                    throw new InvalidDataException("Reconstruction JPEG différente de l'original.");
                return "JPEG reconstruit identique octet pour octet (SHA-256 vérifié), métadonnées incluses.";
            }
            finally { if (File.Exists(reconstructed)) File.Delete(reconstructed); }
        }
        var reference = prepared?.ReferencePath ?? request.SourcePath;
        if (request.Metadata.HasAlpha && await images.AlphaSignatureAsync(reference, prepared?.ReferencePath == null && plan.NormalizesOrientation, ct) !=
            await images.AlphaSignatureAsync(request.DestinationPath, false, ct))
            throw new InvalidDataException("Canal alpha modifié : le résultat est refusé.");
        if (plan.Lossless)
        {
            var sourcePixels = plan.NormalizesOrientation && prepared?.ReferencePath == null
                ? await images.OrientedPixelSignatureAsync(reference, ct) : await images.PixelSignatureAsync(reference, ct);
            if (sourcePixels != await images.PixelSignatureAsync(request.DestinationPath, ct))
                throw new InvalidDataException("Pixels décodés différents : la conversion sans perte est refusée.");
        }
        await metadata.VerifyAsync(request, plan, ct);
        return (plan.Lossless ? "Pixels décodés identiques (signature ImageMagick) ; " : "Image décodée intégralement ; ") +
            "dimensions, alpha et " + (request.Options.KeepMetadata ? "métadonnées descriptives et profil ICC vérifiés. " : "absence de métadonnées descriptives vérifiée. ") + QualityDescription(prepared, plan);
    }
    private static string QualityDescription(PreparedCompressionPlan? prepared, CompressionPlan plan)
    {
        if (plan.Lossless || plan.Reversible) return "Fidélité des pixels vérifiée sans perte.";
        if (plan.IsVideoCopy) return "Flux vidéo copié ; audio traité selon le plan.";
        if (prepared?.QualityReport is { } report)
            return $"{report.Metric} {report.Score.ToString("0.##", CultureInfo.InvariantCulture)}{(report.Sampled ? " sur fenêtres échantillonnées" : " sur l'image entière")} ; objectif mesuré atteint, sans garantie universelle d'invisibilité.";
        return "Encodage manuel : la qualité perceptuelle n'est pas certifiée.";
    }
    public async Task<IReadOnlyList<string>> VideoPacketHashesAsync(string path, CancellationToken ct = default)
    {
        var output = await runner.CheckedAsync(tools.PathFor("ffmpeg"),
            ["-nostdin", "-v", "error", "-i", path, "-map", "0:v", "-c", "copy", "-f", "streamhash", "-hash", "sha256", "-"], ct);
        var hashes = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#'))
            .Select(l => l.Trim().Replace(" ", "", StringComparison.Ordinal)).ToArray();
        if (hashes.Length == 0 || hashes.Any(h => !h.Contains("SHA256=", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Empreinte des paquets vidéo indisponible.");
        return hashes;
    }
    private async Task<IReadOnlyList<string>> CopiedPacketHashesAsync(string path, IEnumerable<int> indices, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-i", path };
        foreach (var index in indices) args.AddRange(["-map", "0:" + index]);
        args.AddRange(["-c", "copy", "-f", "streamhash", "-hash", "sha256", "-"]);
        var text = await runner.CheckedAsync(tools.PathFor("ffmpeg"), args, ct);
        var hashes = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).Select(line => line.Trim()).ToArray();
        if (hashes.Length == 0 || hashes.Any(h => !h.Contains("SHA256=", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Empreinte d'une piste copiée indisponible.");
        return hashes;
    }
    public static void ValidatePhotoMetadata(MediaMetadata source, MediaMetadata output, CompressionPlan plan)
    {
        var swap = plan.NormalizesOrientation && source.Technical.Orientation is >= 5 and <= 8;
        var width = plan.Width > 0 ? plan.Width : swap ? source.Height : source.Width;
        var height = plan.Height > 0 ? plan.Height : swap ? source.Width : source.Height;
        if (output.Width != width || output.Height != height || output.FrameCount != source.FrameCount || (source.HasAlpha && !output.HasAlpha))
            throw new InvalidDataException("Dimensions, nombre d'images ou présence du canal alpha incompatibles avec le plan.");
        var expected = CompressionCodecs.Normalize(plan.CodecId);
        if (!string.IsNullOrEmpty(expected) && expected != "auto" && expected != CompressionCodecs.Normalize(output.Codec))
            throw new InvalidDataException($"Codec image différent du plan : {output.Codec} au lieu de {plan.CodecId}.");
        if (plan.NormalizesOrientation && output.Technical.Orientation != 1) throw new InvalidDataException("Orientation de sortie incohérente avec les pixels normalisés.");
    }
    public static void ValidateVideoMetadata(MediaMetadata source, MediaMetadata output, CompressionPlan plan, bool keepMetadata)
    {
        var expected = plan.IsVideoCopy || string.IsNullOrEmpty(plan.CodecId) ? source.Codec : plan.CodecId;
        if (VideoCodec(expected) != VideoCodec(output.Codec)) throw new InvalidDataException($"Codec vidéo différent du plan : {output.Codec} au lieu de {expected}.");
        if (output.Width != (plan.Width > 0 ? plan.Width : source.Width) || output.Height != (plan.Height > 0 ? plan.Height : source.Height)) throw new InvalidDataException("Dimensions vidéo différentes du plan.");
        var frameRate = FfprobeService.Rational(source.Technical.AverageFrameRate) ?? FfprobeService.Rational(source.Technical.FrameRate) ?? 0;
        var tolerance = Math.Max(250, frameRate > 0 ? 1000 / frameRate : 250);
        if (source.DurationMilliseconds > 0 && Math.Abs(output.DurationMilliseconds - source.DurationMilliseconds) > tolerance)
            throw new InvalidDataException("Durée vidéo modifiée au-delà de la tolérance d'une image / 250 ms.");
        if (source.AudioStreams != output.AudioStreams || source.SubtitleStreams != output.SubtitleStreams || source.TotalStreams != output.TotalStreams)
            throw new InvalidDataException("Une piste vidéo, audio, sous-titre ou pièce jointe a été perdue ou ajoutée.");
        var before = source.Technical;
        var after = output.Technical;
        if (before.Rotation != after.Rotation) throw new InvalidDataException("Matrice de rotation vidéo non conservée.");
        CompareRational(before.SampleAspectRatio, after.SampleAspectRatio, "Ratio des pixels", .0001);
        CompareRational(before.DisplayAspectRatio, after.DisplayAspectRatio, "Ratio d'affichage", .002);
        if (before.BitDepth > 0 && after.BitDepth > 0 && after.BitDepth < before.BitDepth) throw new InvalidDataException("Profondeur vidéo réduite.");
        if (ChromaRank(after.ChromaSubsampling) < ChromaRank(before.ChromaSubsampling)) throw new InvalidDataException("Sous-échantillonnage chromatique accru.");
        CompareColor(before.ColorPrimaries, after.ColorPrimaries, "primaires");
        CompareColor(before.ColorTransfer, after.ColorTransfer, "fonction de transfert");
        CompareColor(before.ColorSpace, after.ColorSpace, "matrice couleur");
        CompareColor(before.ColorRange, after.ColorRange, "plage couleur");
        if (source.Hdr && !output.Hdr) throw new InvalidDataException("Signalisation HDR perdue.");
        if (source.HasAlpha && !output.HasAlpha) throw new InvalidDataException("Canal alpha vidéo perdu.");
        if (before.HasDynamicHdr && !after.HasDynamicHdr) throw new InvalidDataException("Métadonnées HDR dynamiques perdues.");
        if (before.Interlaced != after.Interlaced) throw new InvalidDataException("Structure entrelacée modifiée sans plan validé.");
        if (source.FrameCount > 1 && output.FrameCount > 1 && source.FrameCount != output.FrameCount) throw new InvalidDataException("Nombre d'images vidéo modifié.");
        var outputRate = FfprobeService.Rational(after.AverageFrameRate) ?? 0;
        if (frameRate > 0 && outputRate > 0 && Math.Abs(outputRate - frameRate) > Math.Max(.02, frameRate * .005)) throw new InvalidDataException("Cadence vidéo moyenne modifiée.");
        var sourceStreams = before.Streams.OrderBy(s => s.Index).ToArray();
        var outputStreams = after.Streams.OrderBy(s => s.Index).ToArray();
        if (sourceStreams.Length != outputStreams.Length) throw new InvalidDataException("Inventaire des pistes différent.");
        var primaryVideo = Array.FindIndex(sourceStreams, s => s.CodecType == "video" && !s.AttachedPicture);
        var outputAudio = outputStreams.Where(s => s.CodecType == "audio").ToArray();
        for (var i = 0; i < sourceStreams.Length; i++)
        {
            var s = sourceStreams[i]; var d = outputStreams[i];
            if (s.CodecType != d.CodecType || s.AttachedPicture != d.AttachedPicture) throw new InvalidDataException("Nature ou ordre des pistes modifié.");
            if (s.CodecType != "audio" && i != primaryVideo && s.Codec != d.Codec) throw new InvalidDataException("Codec d'une piste secondaire modifié.");
            if (s.CodecType == "attachment" && (string.IsNullOrEmpty(s.ExtraDataHash) || s.ExtraDataHash != d.ExtraDataHash))
                throw new InvalidDataException("Pièce jointe non conservée à l'identique.");
            if (s.CodecType != "audio" || plan.AudioPlans.Any(a => a.InputIndex == s.Index && a.Copy))
                ValidateSideData(s, d);
            if (s.CodecType == "subtitle" && !string.IsNullOrEmpty(s.ExtraDataHash) && s.ExtraDataHash != d.ExtraDataHash)
                throw new InvalidDataException("En-tête ou styles d'une piste de sous-titres modifiés.");
            if (keepMetadata) ValidateTrackTags(s, d);
        }
        foreach (var audio in plan.AudioPlans)
        {
            var s = sourceStreams.FirstOrDefault(s => s.Index == audio.InputIndex && s.CodecType == "audio") ?? throw new InvalidDataException("Piste audio source du plan introuvable.");
            if (audio.OutputIndex < 0 || audio.OutputIndex >= outputAudio.Length) throw new InvalidDataException("Piste audio de sortie du plan introuvable.");
            var d = outputAudio[audio.OutputIndex];
            if (d.Codec != audio.Codec || d.Channels != audio.Channels || !EquivalentLayout(d.ChannelLayout, audio.ChannelLayout)) throw new InvalidDataException("Codec, canaux ou disposition audio différents du plan.");
            if (!audio.Copy && d.SampleRate != 48000) throw new InvalidDataException("Fréquence Opus de sortie invalide.");
            if (audio.Copy && !string.IsNullOrEmpty(s.ExtraDataHash) && s.ExtraDataHash != d.ExtraDataHash)
                throw new InvalidDataException("Données complémentaires d'une piste audio copiée modifiées.");
            var sourceVideoStart = primaryVideo >= 0 ? sourceStreams[primaryVideo].StartTimeSeconds : 0;
            var outputVideoStart = primaryVideo >= 0 ? outputStreams[primaryVideo].StartTimeSeconds : 0;
            if (Math.Abs((d.StartTimeSeconds - outputVideoStart) - (s.StartTimeSeconds - sourceVideoStart)) > .1) throw new InvalidDataException("Décalage audio/vidéo supérieur à 100 ms.");
            if (s.DurationSeconds is { } sd && d.DurationSeconds is { } dd && sd > 0 && dd > 0 && Math.Abs(sd - dd) > .25) throw new InvalidDataException("Durée d'une piste audio modifiée.");
        }
        if (keepMetadata)
        {
            foreach (var (key, value) in before.Metadata.Where(p => IsUserTag(p.Key)))
                if (!after.Metadata.TryGetValue(key, out var result) || result != value) throw new InvalidDataException("Métadonnée vidéo non conservée : " + key + ".");
            ValidateChapters(before.Metadata.GetValueOrDefault("pixseek:chapters"), after.Metadata.GetValueOrDefault("pixseek:chapters"));
        }
    }
    private static void ValidateSideData(MediaStreamMetadata source, MediaStreamMetadata output)
    {
        if (string.IsNullOrEmpty(source.SideDataJson)) return;
        var before = System.Text.Json.Nodes.JsonNode.Parse(source.SideDataJson)?.AsArray();
        var after = System.Text.Json.Nodes.JsonNode.Parse(output.SideDataJson ?? "[]")?.AsArray();
        if (before == null) return;
        foreach (var item in before)
        {
            // Encoder buffer sizing is regenerated; spatial, colour and other auxiliary data must survive.
            if (item?["side_data_type"]?.GetValue<string>() == "CPB properties") continue;
            if (after == null || !after.Any(value => System.Text.Json.Nodes.JsonNode.DeepEquals(item, value)))
                throw new InvalidDataException("Données auxiliaires de piste non conservées : " + item?["side_data_type"] + ".");
        }
    }
    private static void ValidateTrackTags(MediaStreamMetadata source, MediaStreamMetadata output)
    {
        if (Meaningful(source.Language) && source.Language != output.Language) throw new InvalidDataException("Langue d'une piste perdue.");
        if (!string.IsNullOrEmpty(source.Title) && source.Title != output.Title) throw new InvalidDataException("Titre d'une piste perdu.");
        foreach (var (key, value) in source.Disposition)
            if (value != output.Disposition.GetValueOrDefault(key)) throw new InvalidDataException("Disposition de piste non conservée : " + key + ".");
        foreach (var (key, value) in source.Tags.Where(p => IsUserTag(p.Key)))
            if (!output.Tags.TryGetValue(key, out var result) || value != result) throw new InvalidDataException("Métadonnée de piste non conservée : " + key + ".");
    }
    private static bool EquivalentLayout(string actual, string expected) => string.IsNullOrEmpty(expected) || actual == expected || (expected == "mono" && actual == "1 channels") || (expected == "stereo" && actual == "2 channels");
    private static bool Meaningful(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("unknown" or "unspecified" or "und" or "N/A");
    private static void CompareColor(string before, string after, string label)
    {
        if (Meaningful(before) && before != after) throw new InvalidDataException("Signalisation des " + label + " non conservée.");
    }
    private static int ChromaRank(string value) => value switch { "400" => 0, "420" => 1, "422" => 2, "444" => 3, _ => 0 };
    private static string VideoCodec(string codec) => codec.ToLowerInvariant() switch { "h265" or "h.265" => "hevc", "h.264" => "h264", _ => codec.ToLowerInvariant() };
    private static void CompareRational(string before, string after, string name, double tolerance)
    {
        var original = FfprobeService.Rational(before) ?? 0; var result = FfprobeService.Rational(after) ?? 0;
        if (original > 0 && (result <= 0 || Math.Abs(result - original) > tolerance)) throw new InvalidDataException(name + " non conservé.");
    }
    private static bool IsUserTag(string name) => !name.StartsWith("pixseek:", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("_STATISTICS", StringComparison.OrdinalIgnoreCase) && name.ToLowerInvariant() is not
        ("encoder" or "language" or "major_brand" or "minor_version" or "compatible_brands" or "duration" or "bps" or "number_of_frames" or "number_of_bytes" or "handler_name" or "vendor_id");
    private static void ValidateChapters(string? before, string? after)
    {
        if (string.IsNullOrEmpty(before)) return;
        using var a = JsonDocument.Parse(before); using var b = JsonDocument.Parse(after ?? "[]");
        if (a.RootElement.GetArrayLength() != b.RootElement.GetArrayLength()) throw new InvalidDataException("Chapitres perdus.");
        for (var i = 0; i < a.RootElement.GetArrayLength(); i++)
        {
            var source = a.RootElement[i]; var result = b.RootElement[i];
            foreach (var name in new[] { "start_time", "end_time" })
                if (source.TryGetProperty(name, out var time) && (!result.TryGetProperty(name, out var actual) || !double.TryParse(time.ToString(), CultureInfo.InvariantCulture, out var st) ||
                    !double.TryParse(actual.ToString(), CultureInfo.InvariantCulture, out var dt) || Math.Abs(st - dt) > .01)) throw new InvalidDataException("Horodatage de chapitre non conservé.");
            if (source.TryGetProperty("tags", out var tags)) foreach (var p in tags.EnumerateObject())
                if (!result.TryGetProperty("tags", out var rt) || !rt.TryGetProperty(p.Name, out var value) || value.ToString() != p.Value.ToString()) throw new InvalidDataException("Titre ou métadonnée de chapitre perdu.");
        }
    }
}
