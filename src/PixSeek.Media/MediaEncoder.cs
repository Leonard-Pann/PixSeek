using System.Globalization;
using PixSeek.Core;

namespace PixSeek.Media;
public sealed class MediaEncoder(NativeTools tools, ProcessRunner runner, CompressionProfileResolver resolver, CompressionVerificationService verification) : IMediaEncoder
{
    public string Name => "ImageMagick / libjxl / FFmpeg";

    public async Task<EncodingResult> EncodeAsync(EncodingRequest request, IProgress<EncodingProgress> progress, CancellationToken cancellationToken)
    {
        var originalInfo = new FileInfo(request.SourcePath);
        var original = originalInfo.Length;
        var modified = originalInfo.LastWriteTimeUtc;
        var plan = resolver.Resolve(request);
        if (plan.SkipReason != null)
            return new(request.SourcePath, null, original, original, "Ignoré", plan.SkipReason);
        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
        // The temporary file has a real codec extension; output format is also explicit for magick/ffmpeg.
        var temp = request.DestinationPath + "." + Guid.NewGuid().ToString("N") + ".partial" + plan.Extension;
        var encoding = request with
        {
            DestinationPath = temp
        };
        plan = resolver.Resolve(encoding);
        try
        {
            await runner.CheckedAsync(tools.PathFor(plan.Tool), plan.Arguments, cancellationToken, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) && request.Metadata.DurationMilliseconds > 0)
                    progress.Report(new(Math.Clamp(micros / (request.Metadata.DurationMilliseconds * 1000d), 0, 1), "Encodage AV1"));
            });
            var verified = await verification.VerifyAsync(encoding, plan, cancellationToken);
            originalInfo.Refresh();
            if (!originalInfo.Exists || originalInfo.Length != original || originalInfo.LastWriteTimeUtc != modified)
                throw new IOException("Source modifiée pendant la conversion ; résultat non validé.");
            var size = new FileInfo(temp).Length;
            if (request.Options.KeepOnlySmaller && size >= original)
                return new(request.SourcePath, null, original, size, "Aucun gain", verified);
            cancellationToken.ThrowIfCancellationRequested();
            // Never replace an existing output, including a previous conversion.
            File.Move(temp, request.DestinationPath, false);
            try
            {
                File.SetLastWriteTimeUtc(request.DestinationPath, modified);
            }
            catch (Exception e)when (e is IOException or UnauthorizedAccessException)
            {
            }

            progress.Report(new(1, "Vérifié"));
            return new(request.SourcePath, request.DestinationPath, original, size, "Converti", verified);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return new(request.SourcePath, null, original, original, "Échec", "Non validé", e.Message);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
