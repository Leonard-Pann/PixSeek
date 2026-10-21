using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PixSeek.Core;
using PixSeek.Infrastructure;

namespace PixSeek.Media;
public sealed record FolderAnalysis(int Photos, int Videos, long Bytes, IReadOnlyDictionary<string, int> Formats);
public sealed class MediaCompressionService(LibraryDatabase db, ImageMagickService images, FfprobeService probe, MediaEncoder encoder, CompressionProfileResolver resolver)
{
    public async Task<FolderAnalysis> AnalyzeAsync(string directory, bool recursive, CancellationToken ct, string? destinationDirectory = null)
    {
        var excluded = (await db.CompressionDestinationsAsync(ct)).Concat(destinationDirectory == null ? [] : new[] { Path.GetFullPath(destinationDirectory) }).ToArray();
        return await Task.Run(() =>
        {
            var photos = 0;
            var videos = 0;
            long bytes = 0;
            var formats = new Dictionary<string, int>();
            foreach (var f in FileDiscovery.Enumerate(directory, recursive, ct, excluded))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                formats[ext] = formats.GetValueOrDefault(ext) + 1;
                bytes += new FileInfo(f).Length;
                if (MediaFormats.TypeOf(f) == MediaType.Image)
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
        var destination = Path.GetFullPath(destinationDirectory ?? Path.Combine(Path.GetFullPath(directory), MediaFormats.OutputFolder));
        if (string.Equals(Path.TrimEndingDirectorySeparator(destination), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Le dossier de sortie doit être différent du dossier d'entrée.");
        Directory.CreateDirectory(destination);
        var excluded = (await db.CompressionDestinationsAsync(ct)).Append(destination).ToArray();
        var files = selectedFiles?.Select(Path.GetFullPath).Distinct().ToArray() ?? await Task.Run(() => FileDiscovery.Enumerate(directory, options.Recursive, ct, excluded).ToArray(), ct);
        var results = new List<EncodingResult>();
        await using var c = await db.OpenAsync(ct);
        long job;
        var optionsJson = JsonSerializer.Serialize(new { Options = options, Selection = selectedFiles?.OrderBy(p => p, StringComparer.Ordinal).ToArray() });
        // Reuse only interrupted jobs with identical options and the same source folder.
        using (var find = c.CreateCommand())
        {
            find.CommandText = "SELECT Id FROM CompressionJob WHERE SourceDirectory=$s AND DestinationDirectory=$d AND OptionsJson=$o AND Status IN ('Interrompu','Annulé') ORDER BY Id DESC LIMIT 1";
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
                var type = MediaFormats.TypeOf(file);
                EncodingResult result;
                try
                {
                    var metadata = type == MediaType.Image ? await images.IdentifyAsync(file, ct) : await probe.ProbeAsync(file, ct);
                    var relative = Path.GetRelativePath(directory, file);
                    if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                        relative = Path.Combine("Sélection", Path.GetFileName(file));
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file))))[..10].ToLowerInvariant();
                    var stem = Path.Combine(destination, Path.GetDirectoryName(relative) ?? "", Path.GetFileNameWithoutExtension(relative) + "-" + Path.GetExtension(relative).TrimStart('.') + "-" + hash);
                    var request = new EncodingRequest(file, stem, type, metadata, options);
                    var plan = resolver.Resolve(request);
                    var final = stem + plan.Extension;
                    using var previous = c.CreateCommand();
                    previous.CommandText = "SELECT Payload FROM CompressionItem WHERE JobId=$j AND SourcePath=$s AND Status='Converti'";
                    previous.Parameters.AddWithValue("$j", job);
                    previous.Parameters.AddWithValue("$s", file);
                    var payload = await previous.ExecuteScalarAsync(ct);
                    var old = payload is string json ? JsonSerializer.Deserialize<EncodingResult>(json) : null;
                    if (old != null && old.DestinationPath != null && File.Exists(old.DestinationPath) && new FileInfo(old.DestinationPath).Length == old.OutputBytes && new FileInfo(file).Length == old.OriginalBytes && File.GetLastWriteTimeUtc(old.DestinationPath) == File.GetLastWriteTimeUtc(file))
                        result = old;
                    else if (File.Exists(final))
                        result = new(file, null, new FileInfo(file).Length, new FileInfo(file).Length, "Ignoré", "Sortie existante conservée ; aucun écrasement.");
                    else
                        result = await encoder.EncodeAsync(request with { DestinationPath = final }, new Progress<EncodingProgress>(p => progress.Report(new($"{Path.GetFileName(file)} · {p.Message}", index, files.Length, (index + p.Fraction) / Math.Max(1, files.Length), Interlocked.Read(ref saved)))), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    var size = File.Exists(file) ? new FileInfo(file).Length : 0;
                    result = new(file, null, size, size, "Échec", "Non validé", e.Message);
                }

                results.Add(result);
                Interlocked.Add(ref saved, result.SavedBytes);
                using var item = c.CreateCommand();
                item.CommandText = "INSERT INTO CompressionItem(JobId,SourcePath,DestinationPath,OriginalSizeBytes,OutputSizeBytes,Status,ErrorMessage,VerificationStatus,Payload) VALUES($j,$s,$d,$b,$a,$status,$e,$v,$json) ON CONFLICT(JobId,SourcePath) DO UPDATE SET DestinationPath=$d,OutputSizeBytes=$a,Status=$status,ErrorMessage=$e,VerificationStatus=$v,Payload=$json";
                foreach (var(k, v)in new (string, object? )[]
                {
                    ("j", job),
                    ("s", file),
                    ("d", result.DestinationPath),
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

            var report = new CompressionReport(job, destination, results);
            using var finish = c.CreateCommand();
            finish.CommandText = "UPDATE CompressionJob SET Status='Terminé',CompletedAt=CURRENT_TIMESTAMP,OriginalSizeBytes=$o,ResultSizeBytes=$r,SavedBytes=$s WHERE Id=$id";
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
}
