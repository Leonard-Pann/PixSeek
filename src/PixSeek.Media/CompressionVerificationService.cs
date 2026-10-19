using System.Security.Cryptography;
using PixSeek.Core;

namespace PixSeek.Media;
public sealed class CompressionVerificationService(ImageMagickService images, FfprobeService probe, FfmpegService ffmpeg, NativeTools tools, ProcessRunner runner)
{
    public async Task<string> VerifyAsync(EncodingRequest request, CompressionPlan plan, CancellationToken ct)
    {
        if (!File.Exists(request.DestinationPath) || new FileInfo(request.DestinationPath).Length == 0)
            throw new InvalidDataException("Sortie vide ou absente.");
        if (request.MediaType == MediaType.Video)
        {
            var m = await probe.ProbeAsync(request.DestinationPath, ct);
            var original = request.Metadata;
            if (m.Codec != "av1" || m.Width != original.Width || m.Height != original.Height || Math.Abs(m.DurationMilliseconds - original.DurationMilliseconds) > Math.Max(500, original.DurationMilliseconds * .01) || m.AudioStreams != original.AudioStreams || m.SubtitleStreams != original.SubtitleStreams || m.TotalStreams != original.TotalStreams)
                throw new InvalidDataException("La validation des dimensions, durée ou pistes a échoué.");
            await ffmpeg.VerifyDecodeAsync(request.DestinationPath, ct);
            return "AV1 décodé intégralement ; dimensions, durée (tolérance max. 500 ms / 1 %), nombre de pistes vérifiés. Vidéo avec perte.";
        }

        var result = await images.IdentifyAsync(request.DestinationPath, ct);
        if (result.Width != request.Metadata.Width || result.Height != request.Metadata.Height || result.FrameCount != request.Metadata.FrameCount || (request.Metadata.HasAlpha && !result.HasAlpha))
            throw new InvalidDataException("Dimensions, frames ou alpha modifiés.");
        // Force a full decode in every mode, not only identify -ping.
        _ = await images.PixelSignatureAsync(request.DestinationPath, ct);
        if (plan.Reversible)
        {
            var reconstructed = request.DestinationPath + ".reconstructed.jpg";
            try
            {
                await runner.CheckedAsync(tools.PathFor("djxl"), [request.DestinationPath, reconstructed], ct);
                await using var src = File.OpenRead(request.SourcePath);
                await using var dst = File.OpenRead(reconstructed);
                var before = await SHA256.HashDataAsync(src, ct);
                var after = await SHA256.HashDataAsync(dst, ct);
                if (!before.SequenceEqual(after))
                    throw new InvalidDataException("Reconstruction JPEG différente de l'original.");
                return "JPEG reconstruit identique octet pour octet (SHA-256 vérifié).";
            }
            finally
            {
                if (File.Exists(reconstructed))
                    File.Delete(reconstructed);
            }
        }

        if (plan.Lossless)
        {
            if (await images.PixelSignatureAsync(request.SourcePath, ct) != await images.PixelSignatureAsync(request.DestinationPath, ct))
                throw new InvalidDataException("Pixels décodés différents : la conversion sans perte est refusée.");
        }

        if (request.Options.KeepMetadata)
        {
            var before = await images.CriticalMetadataAsync(request.SourcePath, ct);
            var after = await images.CriticalMetadataAsync(request.DestinationPath, ct);
            if (before["captureDate"] != after["captureDate"])
                throw new InvalidDataException("Date de prise de vue non conservée.");
            if (before["iccHash"].Length > 0 && before["iccHash"] != after["iccHash"])
                throw new InvalidDataException("Profil ICC non conservé à l'identique.");
        }

        return plan.Lossless ? "Pixels décodés identiques (signature ImageMagick), dimensions, alpha et métadonnées critiques vérifiés." : "Image décodée ; dimensions, alpha et métadonnées critiques vérifiés. Encodage perceptuel avec perte.";
    }
}
