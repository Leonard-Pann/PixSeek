using System.Security.Cryptography;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Exports an original without depending on decoders, codecs or metadata support.</summary>
public sealed class OriginalMediaExporter
{
    public async Task<EncodingResult> CopyAsync(EncodingRequest request, string reason, IProgress<EncodingProgress> progress, CancellationToken ct)
    {
        var sourcePath = Path.GetFullPath(request.SourcePath);
        var requested = Path.GetFullPath(request.OriginalDestinationPath ??
            (request.DestinationIsStem ? request.DestinationPath + Path.GetExtension(sourcePath) : Path.ChangeExtension(request.DestinationPath, Path.GetExtension(sourcePath))));
        if (SamePath(sourcePath, requested)) throw new IOException("La sortie ne peut pas être le fichier source.");
        var directory = Path.GetDirectoryName(requested)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".pixseek-copy-" + Guid.NewGuid().ToString("N") + ".partial");
        var before = new FileInfo(sourcePath);
        var length = before.Length; var modified = before.LastWriteTimeUtc; var created = before.CreationTimeUtc;
        progress.Report(new(.85, "Copie intégrale du fichier original : " + reason) { Phase = "Copying" });
        try
        {
            string sourceHash;
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await input.CopyToAsync(output, ct);
                input.Position = 0;
                sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            }
            var outputHash = await HashAsync(temporary, ct);
            before.Refresh();
            if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != modified || new FileInfo(temporary).Length != length || sourceHash != outputHash)
                throw new IOException("La copie de l'original n'est pas vérifiée ou la source a changé ; aucun export validé.");
            var final = requested;
            for (var collision = 0; ; collision++)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(final))
                {
                    if ((File.GetAttributes(final) & FileAttributes.ReparsePoint) == 0 && new FileInfo(final).Length == length && await HashAsync(final, ct) == sourceHash)
                        return Result(final, sourceHash, outputHash);
                    final = CollisionPath(requested, sourceHash, collision);
                    if (SamePath(final, sourcePath)) throw new IOException("Collision avec le fichier source.");
                    continue;
                }
                // File.Move never overwrites an existing export or a user's unrelated file.
                try { File.Move(temporary, final, false); }
                catch (IOException) when (File.Exists(final)) { continue; }
                try { File.SetLastWriteTimeUtc(final, modified); File.SetCreationTimeUtc(final, created); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
                progress.Report(new(1, "Original copié et vérifié SHA-256") { Phase = "Completed", EstimatedRemaining = TimeSpan.Zero });
                return Result(final, sourceHash, outputHash);
            }

            EncodingResult Result(string path, string srcHash, string dstHash) => new(request.SourcePath, path, length, length, "Copié",
                "Copie identique octet pour octet, SHA-256 vérifié : toutes les pistes et métadonnées incorporées sont conservées. " + reason)
            { SourceSha256 = srcHash, OutputSha256 = dstHash };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    public static async Task<bool> IsVerifiedAsync(EncodingResult result, CancellationToken ct = default)
    {
        if (!result.IsExported || result.DestinationPath == null || string.IsNullOrEmpty(result.OutputSha256) || string.IsNullOrEmpty(result.SourceSha256)) return false;
        try
        {
            return File.Exists(result.DestinationPath) && (File.GetAttributes(result.DestinationPath) & FileAttributes.ReparsePoint) == 0 &&
                !SamePath(result.SourcePath, result.DestinationPath) && new FileInfo(result.DestinationPath).Length == result.OutputBytes &&
                await HashAsync(result.DestinationPath, ct) == result.OutputSha256 && await HashAsync(result.SourcePath, ct) == result.SourceSha256;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    private static string CollisionPath(string requested, string hash, int collision) => Path.Combine(Path.GetDirectoryName(requested)!,
        Path.GetFileNameWithoutExtension(requested) + "-original-" + hash[..12] + (collision == 0 ? "" : "-" + collision) + Path.GetExtension(requested));
    private static bool SamePath(string a, string b) => string.Equals(ExportPathPolicy.ResolvePath(a), ExportPathPolicy.ResolvePath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
