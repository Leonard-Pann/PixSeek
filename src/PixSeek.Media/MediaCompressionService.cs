using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PixSeek.Core;
using PixSeek.Infrastructure;

namespace PixSeek.Media;
public sealed record FolderAnalysis(int Photos, int Videos, long Bytes, IReadOnlyDictionary<string, int> Formats);
public sealed class MediaCompressionService
{
    private readonly LibraryDatabase db;
    private readonly MediaEncoder encoder;
    public MediaCompressionService(LibraryDatabase db, ImageMagickService images, FfprobeService probe, MediaEncoder encoder, CompressionProfileResolver resolver)
    {
        this.db = db;
        this.encoder = encoder;
    }
    public async Task<FolderAnalysis> AnalyzeAsync(string directory, bool recursive, CancellationToken ct, string? destinationDirectory = null)
    {
        var excluded = destinationDirectory == null ? Array.Empty<string>() : new[] { Path.GetFullPath(destinationDirectory) };
        return await Task.Run(() =>
        {
            var photos = 0;
            var videos = 0;
            long bytes = 0;
            var formats = new Dictionary<string, int>();
            foreach (var f in ExportFileDiscovery.Enumerate(directory, recursive, ct, excluded))
            {
                if (ExportFileDiscovery.IsSidecar(f)) continue;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                formats[ext] = formats.GetValueOrDefault(ext) + 1;
                bytes += new FileInfo(f).Length;
                if (ExportFileDiscovery.TypeOf(f) == MediaType.Image)
                    photos++;
                else
                    videos++;
            }

            return new FolderAnalysis(photos, videos, bytes, formats);
        }, ct);
    }

    public async Task<CompressionReport> RunAsync(string directory, CompressionOptions options, JobControl control, IProgress<WorkProgress> progress, IReadOnlyList<string>? selectedFiles = null, string? destinationDirectory = null)
    {
        var ct = control.Token;
        options = options with { KeepMetadata = true, Recursive = selectedFiles == null || options.Recursive };
        var destination = Path.GetFullPath(destinationDirectory ?? ExportPathPolicy.DefaultDestination(directory));
        ExportPathPolicy.EnsureIndependent(directory, destination);
        Directory.CreateDirectory(destination);
        // Historical exports inside the source are still source media and must not be silently omitted.
        var excluded = new[] { destination };
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = selectedFiles != null ? WithCompanions(selectedFiles, comparer).ToArray() : await Task.Run(() => ExportFileDiscovery.Enumerate(directory, options.Recursive, ct, excluded).Order(comparer).ToArray(), ct);
        var sourceSnapshot = files.ToDictionary(f => f, MediaEncoder.Fingerprint, comparer);
        var results = new List<EncodingResult>();
        await using var c = await db.OpenAsync(ct);
        long job;
        var optionsJson = JsonSerializer.Serialize(new { AlgorithmVersion = AdaptiveCompressionPolicy.Version, Capabilities = encoder.CapabilitiesFingerprint, Options = options, Selection = selectedFiles?.OrderBy(p => p, StringComparer.Ordinal).ToArray() });
        // Reuse only interrupted jobs with identical options and the same source folder.
        using (var find = c.CreateCommand())
        {
            find.CommandText = "SELECT Id FROM CompressionJob WHERE SourceDirectory=$s AND DestinationDirectory=$d AND OptionsJson=$o AND Status IN ('Interrompu','Annulé','Incomplet') ORDER BY Id DESC LIMIT 1";
            find.Parameters.AddWithValue("$d", destination);
            find.Parameters.AddWithValue("$s", Path.GetFullPath(directory));
            find.Parameters.AddWithValue("$o", optionsJson);
            var existing = await find.ExecuteScalarAsync(ct);
            job = existing is long id ? id : 0;
        }

        if (job == 0)
        {
            using var create = c.CreateCommand();
            create.CommandText = "INSERT INTO CompressionJob(SourceDirectory,DestinationDirectory,Preset,Status,StartedAt,OptionsJson) VALUES($s,$d,$p,'En cours',CURRENT_TIMESTAMP,$o) RETURNING Id";
            create.Parameters.AddWithValue("$s", Path.GetFullPath(directory));
            create.Parameters.AddWithValue("$d", destination);
            create.Parameters.AddWithValue("$p", options.Preset.ToString());
            create.Parameters.AddWithValue("$o", optionsJson);
            job = (long)(await create.ExecuteScalarAsync(ct))!;
        }

        using (var start = c.CreateCommand())
        {
            start.CommandText = "UPDATE CompressionJob SET Status='En cours',StartedAt=CURRENT_TIMESTAMP WHERE Id=$id";
            start.Parameters.AddWithValue("$id", job);
            await start.ExecuteNonQueryAsync(ct);
        }

        var clock = Stopwatch.StartNew();
        long saved = 0;
        try
        {
            foreach (var file in files)
            {
                await control.CheckpointAsync();
                var index = results.Count;
                var baseProgress = files.Length > 0 ? index / (double)files.Length : 0;
                progress.Report(new($"{Path.GetFileName(file)} · {index}/{files.Length} · {clock.Elapsed:hh\\:mm\\:ss}", index, files.Length, baseProgress, Interlocked.Read(ref saved)));
                var type = ExportFileDiscovery.TypeOf(file);
                EncodingResult result;
                EncodingRequest? request = null;
                try
                {
                    var relative = Path.GetRelativePath(directory, file);
                    if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                        relative = Path.Combine("Sélection", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetDirectoryName(file)!)))[..10].ToLowerInvariant(), Path.GetFileName(file));
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file))))[..10].ToLowerInvariant();
                    var stem = Path.Combine(destination, Path.GetDirectoryName(relative) ?? "", Path.GetFileNameWithoutExtension(relative) + "-" + Path.GetExtension(relative).TrimStart('.') + "-" + hash);
                    ExportPathPolicy.EnsureIndependent(directory, Path.GetDirectoryName(stem)!);
                    // The encoder analyzes and prepares this request once, including the final extension.
                    request = new EncodingRequest(file, stem, type, new(0, 0, 0, ""), options)
                    { DestinationIsStem = true, OriginalDestinationPath = Path.Combine(destination, relative) };
                    using var previous = c.CreateCommand();
                    previous.CommandText = "SELECT Payload FROM CompressionItem WHERE JobId=$j AND SourcePath=$s AND Status IN ('Converti','Copié')";
                    previous.Parameters.AddWithValue("$j", job);
                    previous.Parameters.AddWithValue("$s", file);
                    var payload = await previous.ExecuteScalarAsync(ct);
                    var old = payload is string json ? JsonSerializer.Deserialize<EncodingResult>(json) : null;
                    if (old != null && await OriginalMediaExporter.IsVerifiedAsync(old, ct))
                        result = old;
                    else if (!MediaFormats.IsMedia(file))
                        result = await new OriginalMediaExporter().CopyAsync(request,
                            ExportFileDiscovery.IsSidecar(file) ? "Fichier annexe de métadonnées ou de sous-titres." : "Format source sans chaîne de réencodage validée.", new Progress<EncodingProgress>(), ct);
                    else
                        result = await encoder.EncodeAsync(request, new Progress<EncodingProgress>(p => progress.Report(new($"{Path.GetFileName(file)} · {p.Message}", index, files.Length, (index + p.Fraction) / Math.Max(1, files.Length), Interlocked.Read(ref saved))
                        { Phase = p.Phase, EstimatedRemaining = p.EstimatedRemaining })), ct, _ => control.CheckpointAsync());
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    try
                    {
                        if (request == null) throw new IOException("Destination d'export non résolue.");
                        result = await new OriginalMediaExporter().CopyAsync(request, "Traitement impossible : " + e.Message, new Progress<EncodingProgress>(), ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception copyError)
                    {
                        var size = File.Exists(file) ? new FileInfo(file).Length : 0;
                        result = new(file, null, size, size, "Échec", "Export incomplet.", e.Message + " ; copie : " + copyError.Message);
                    }
                }

                results.Add(result);
                Interlocked.Add(ref saved, result.SavedBytes);
                using var item = c.CreateCommand();
                item.CommandText = "INSERT INTO CompressionItem(JobId,SourcePath,DestinationPath,SourceCodec,TargetCodec,OriginalSizeBytes,OutputSizeBytes,Status,ErrorMessage,VerificationStatus,Payload) VALUES($j,$s,$d,$sourceCodec,$targetCodec,$b,$a,$status,$e,$v,$json) ON CONFLICT(JobId,SourcePath) DO UPDATE SET DestinationPath=$d,SourceCodec=$sourceCodec,TargetCodec=$targetCodec,OutputSizeBytes=$a,Status=$status,ErrorMessage=$e,VerificationStatus=$v,Payload=$json";
                foreach (var(k, v)in new (string, object? )[]
                {
                    ("j", job),
                    ("s", file),
                    ("d", result.DestinationPath),
                    ("sourceCodec", result.Decision?.SourceCodecId),
                    ("targetCodec", result.Decision?.CodecId),
                    ("b", result.OriginalBytes),
                    ("a", result.OutputBytes),
                    ("status", result.Status),
                    ("e", result.Error),
                    ("v", result.Verification),
                    ("json", JsonSerializer.Serialize(result))
                }

                )
                    item.Parameters.AddWithValue("$" + k, v ?? DBNull.Value);
                await item.ExecuteNonQueryAsync(ct);
            }

            string? completionIssue = null;
            for (var i = 0; i < results.Count; i++)
                if (results[i].IsExported && !await OriginalMediaExporter.IsVerifiedAsync(results[i], ct))
                    results[i] = results[i] with { Status = "Échec", Error = "Source ou sortie modifiée après l'export ; copie à vérifier." };
            if (results.Any(r => !r.IsExported)) completionIssue = "Un ou plusieurs fichiers n'ont pas pu être exportés. Le dossier source doit être conservé.";
            if (selectedFiles == null)
            {
                var current = ExportFileDiscovery.Enumerate(directory, options.Recursive, ct, excluded).ToHashSet(comparer);
                if (!current.SetEquals(files) || files.Any(f => !File.Exists(f) || MediaEncoder.Fingerprint(f) != sourceSnapshot[f]))
                    completionIssue = "Le dossier source a changé pendant l'export. Relancez l'export avant de supprimer des sources.";
            }
            var report = new CompressionReport(job, destination, results) { CompletionIssue = completionIssue };
            using var finish = c.CreateCommand();
            finish.CommandText = "UPDATE CompressionJob SET Status=$status,CompletedAt=CURRENT_TIMESTAMP,OriginalSizeBytes=$o,ResultSizeBytes=$r,SavedBytes=$s WHERE Id=$id";
            finish.Parameters.AddWithValue("$status", report.IsComplete ? "Terminé" : "Incomplet");
            finish.Parameters.AddWithValue("$o", report.OriginalBytes);
            finish.Parameters.AddWithValue("$r", report.FinalBytes);
            finish.Parameters.AddWithValue("$s", report.SavedBytes);
            finish.Parameters.AddWithValue("$id", job);
            await finish.ExecuteNonQueryAsync(ct);
            return report;
        }
        catch
        {
            using var interrupted = c.CreateCommand();
            interrupted.CommandText = "UPDATE CompressionJob SET Status='Interrompu' WHERE Id=$id";
            interrupted.Parameters.AddWithValue("$id", job);
            await interrupted.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }
    private static IEnumerable<string> WithCompanions(IReadOnlyList<string> selected, StringComparer comparer)
    {
        var files = new HashSet<string>(selected.Select(Path.GetFullPath), comparer);
        foreach (var source in selected.Select(Path.GetFullPath))
        {
            var stem = Path.GetFileNameWithoutExtension(source);
            foreach (var candidate in Directory.EnumerateFiles(Path.GetDirectoryName(source)!))
                if (ExportFileDiscovery.IsSidecar(candidate) && Path.GetFileName(candidate).StartsWith(stem + ".", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    files.Add(candidate);
        }
        return files.Order(comparer);
    }
}
