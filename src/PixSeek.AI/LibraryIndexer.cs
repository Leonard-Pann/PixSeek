using System.Threading.Channels;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.Logging;

namespace PixSeek.AI;
public sealed class LibraryIndexer(LibraryDatabase db, ImageMagickService images, FfprobeService probe, ThumbnailService thumbnails, FfmpegService videos, OnnxEmbeddingService embeddings, ModelManager models, SettingsStore settings, ILogger<LibraryIndexer> logger)
{
    private readonly SemaphoreSlim _singleScan = new(1, 1);
    private sealed record PlannedMedia(string Path, MediaFile? Existing, MediaMetadata? Metadata, int Units);
    private sealed record PlannedFolder(LibraryFolder Folder, IReadOnlyList<PlannedMedia> Items);
    public async Task ScanAsync(IReadOnlyList<LibraryFolder> folders, JobControl control, IProgress<WorkProgress> progress, bool force = false)
    {
        await _singleScan.WaitAsync(control.Token);
        try
        {
            var ct = control.Token;
            progress.Report(new("Préparation de l'indexation…", IsIndeterminate: true));
            var ai = models.FilesPresent;
            if (ai)
            {
                progress.Report(new("Chargement du modèle local…", IsIndeterminate: true));
                await embeddings.InitializeAsync(ct);
                await db.ConfigureVectorsAsync(models.Version, models.Manifest.Dimensions, ct);
            }

            var excluded = await db.CompressionDestinationsAsync(ct);
            var plans = new List<PlannedFolder>();
            var discovered = 0;
            // Finish discovery before calculating a percentage. The denominator then stays fixed.
            foreach (var folder in folders)
            {
                await control.CheckpointAsync();
                if (!Directory.Exists(folder.Path))
                {
                    await db.SetFolderStatusAsync(folder.Id, "Indisponible", ct);
                    continue;
                }

                var items = new List<PlannedMedia>();
                foreach (var path in FileDiscovery.Enumerate(folder.Path, folder.Recursive, ct, excluded))
                {
                    await control.CheckpointAsync();
                    discovered++;
                    if (discovered % 20 == 1 || MediaFormats.TypeOf(path) == MediaType.Video)
                        progress.Report(new($"Préparation · {discovered} médias · {Path.GetFileName(path)}", IsIndeterminate: true));
                    var old = await db.FindAsync(path, ct);
                    var info = new FileInfo(path);
                    var unchanged = old != null && info.Exists && old.SizeBytes == info.Length && old.LastModifiedAt == info.LastWriteTimeUtc;
                    var ready = unchanged && !force && old!.Status is not ("Erreur" or "Absent") && File.Exists(old.ThumbnailPath) && (!ai || old.IndexedVersion == models.Version);
                    MediaMetadata? metadata = null;
                    // Already indexed files do not inflate the percentage before new work starts.
                    var units = ready ? 0 : ai ? 4 : 2;
                    if (!ready && ai && MediaFormats.TypeOf(path) == MediaType.Video)
                    {
                        try
                        {
                            metadata = await probe.ProbeAsync(path, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            logger.LogDebug(e, "Préparation vidéo impossible : {Path}", path);
                        }

                        var timestamps = FfmpegService.FrameTimestamps(metadata?.DurationMilliseconds ?? old?.DurationMilliseconds ?? 0, settings.Current.FrameIntervalSeconds, settings.Current.MaxVideoFrames);
                        units = 3 + 2 * timestamps.Count;
                    }

                    items.Add(new(path, old, metadata, units));
                }

                plans.Add(new(folder, items));
            }

            var total = plans.Sum(p => p.Items.Sum(i => i.Units) + 1);
            var completed = 0;
            var completedFiles = 0;
            progress.Report(new($"Indexation · 0/{discovered} médias", 0, total, 0));
            foreach (var plan in plans)
            {
                var folder = plan.Folder;
                await db.SetFolderStatusAsync(folder.Id, "Analyse en cours", ct);
                var queue = Channel.CreateBounded<PlannedMedia>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
                using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var producer = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var item in plan.Items)
                            await queue.Writer.WriteAsync(item, producerCancellation.Token);
                        queue.Writer.TryComplete();
                    }
                    catch (Exception e)
                    {
                        queue.Writer.TryComplete(e);
                        throw;
                    }
                }, producerCancellation.Token);
                try
                {
                    await foreach (var item in queue.Reader.ReadAllAsync(ct))
                    {
                        await control.CheckpointAsync();
                        var itemStart = completed;
                        var previousFraction = 0d;
                        void ReportItem(double fraction, string message)
                        {
                            previousFraction = Math.Max(previousFraction, Math.Clamp(fraction, 0, 1));
                            var done = itemStart + item.Units * previousFraction;
                            progress.Report(new($"{message} · {completedFiles}/{discovered} médias", (int)done, total, total == 0 ? 0 : done / total));
                        }

                        await ProcessFileAsync(folder, item, ai, force, control, ReportItem);
                        completed += item.Units;
                        completedFiles++;
                        progress.Report(new($"{completedFiles}/{discovered} médias analysés", completed, total, total == 0 ? 0 : completed / (double)total));
                    }

                    await producer;
                    if (Directory.Exists(folder.Path))
                    {
                        var seen = plan.Items.Select(i => i.Path).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                        var missing = new List<MediaFile>();
                        for (var offset = 0;; offset += 240)
                        {
                            var page = await db.MediaAsync(offset, 240, folder: folder.Id, ct: ct);
                            if (page.Count == 0)
                                break;
                            foreach (var media in page)
                                if (!seen.Contains(media.FullPath) && (!File.Exists(media.FullPath) || (!folder.Recursive && Path.GetDirectoryName(media.FullPath) != folder.Path)))
                                    missing.Add(media);
                        }

                        foreach (var media in missing)
                            await db.SaveMediaAsync(media with { Status = "Absent", ErrorMessage = "Fichier absent ou hors du périmètre du scan." }, ct);
                    }

                    await db.SetFolderStatusAsync(folder.Id, "Prêt", ct);
                    completed++;
                    progress.Report(new($"Bibliothèque {folder.DisplayName} mise à jour", completed, total, total == 0 ? 0 : completed / (double)total));
                }
                catch
                {
                    producerCancellation.Cancel();
                    try
                    {
                        await producer;
                    }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "Arrêt du producteur");
                    }

                    await db.SetFolderStatusAsync(folder.Id, "Interrompu");
                    throw;
                }
            }

            progress.Report(new(ai ? "Analyse et indexation terminées" : "Miniatures prêtes · Téléchargez le modèle pour activer la recherche", total, total, 1));
        }
        finally
        {
            _singleScan.Release();
        }
    }

    private async Task ProcessFileAsync(LibraryFolder folder, PlannedMedia item, bool ai, bool force, JobControl control, Action<double, string> report)
    {
        var path = item.Path;
        var ct = control.Token;
        MediaFile? media = null;
        try
        {
            var info = new FileInfo(path);
            var old = item.Existing;
            var unchanged = old != null && info.Exists && old.SizeBytes == info.Length && old.LastModifiedAt == info.LastWriteTimeUtc;
            if (unchanged && !force && old!.Status is not ("Erreur" or "Absent") && File.Exists(old.ThumbnailPath) && (!ai || old.IndexedVersion == models.Version))
            {
                report(1, "Déjà indexé · " + info.Name);
                return;
            }

            if (old != null && !unchanged)
                await db.InvalidateVectorsAsync(old.Id, ct);
            media = unchanged ? old! : new()
            {
                LibraryFolderId = folder.Id,
                FullPath = info.FullName,
                SizeBytes = info.Length,
                LastModifiedAt = info.LastWriteTimeUtc,
                MediaType = MediaFormats.TypeOf(path)
            };
            var metadata = item.Metadata ?? (media.MediaType == MediaType.Image ? await images.IdentifyAsync(path, ct) : await probe.ProbeAsync(path, ct));
            var timestamps = media.MediaType == MediaType.Video && ai ? FfmpegService.FrameTimestamps(metadata.DurationMilliseconds, settings.Current.FrameIntervalSeconds, settings.Current.MaxVideoFrames) : [];
            var stages = !ai ? 2 : media.MediaType == MediaType.Video ? 3 + 2 * timestamps.Count : 4;
            var stage = 1;
            report(stage / (double)stages, "Métadonnées · " + info.Name);
            media = media with
            {
                Width = metadata.Width,
                Height = metadata.Height,
                DurationMilliseconds = metadata.DurationMilliseconds,
                Codec = metadata.Codec,
                HasAlpha = metadata.HasAlpha,
                FrameCount = metadata.FrameCount,
                ErrorMessage = null,
                Status = "Miniature prête",
                IndexedVersion = unchanged ? media.IndexedVersion : null
            };
            media = await db.SaveMediaAsync(media, ct);
            var thumb = await thumbnails.CreateAsync(media, ct);
            media = await db.SaveMediaAsync(media with { ThumbnailPath = thumb }, ct);
            report(++stage / (double)stages, "Miniature · " + info.Name);
            if (!ai)
                return;
            var vectors = new List<FrameVector>();
            if (media.MediaType == MediaType.Image)
            {
                vectors.Add(new(0, thumb, await embeddings.GenerateImageEmbeddingAsync(path, ct)));
                report(++stage / (double)stages, "Analyse IA · " + info.Name);
            }
            else
            {
                for (var index = 0; index < timestamps.Count; index++)
                {
                    await control.CheckpointAsync();
                    var timestamp = timestamps[index];
                    var frame = thumbnails.CachePath(media, "frame-" + timestamp);
                    var label = $"{info.Name} · image {index + 1}/{timestamps.Count}";
                    if (!File.Exists(frame))
                    {
                        var temporaryFrame = frame + ".partial.jpg";
                        try
                        {
                            await videos.ExtractFrameAsync(path, timestamp, temporaryFrame, 640, ct);
                            File.Move(temporaryFrame, frame, true);
                        }
                        finally
                        {
                            if (File.Exists(temporaryFrame))
                                File.Delete(temporaryFrame);
                        }
                    }

                    report(++stage / (double)stages, "Extraction · " + label);
                    vectors.Add(new(timestamp, frame, await embeddings.GenerateImageEmbeddingAsync(frame, ct)));
                    report(++stage / (double)stages, "Analyse IA · " + label);
                }
            }

            info.Refresh();
            if (!info.Exists || info.Length != media.SizeBytes || info.LastWriteTimeUtc != media.LastModifiedAt)
                throw new IOException("Le fichier a changé pendant l'analyse. Rescannez le dossier.");
            await db.SaveVectorsAsync(media, models.Version, vectors, ct);
            await db.SaveMediaAsync(media with { Status = "Indexé", IndexedVersion = models.Version }, ct);
            report(1, "Indexé · " + info.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Analyse impossible : {Path}", path);
            if (media == null)
            {
                var info = new FileInfo(path);
                media = new()
                {
                    LibraryFolderId = folder.Id,
                    FullPath = path,
                    MediaType = MediaFormats.TypeOf(path),
                    SizeBytes = info.Exists ? info.Length : 0,
                    LastModifiedAt = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow
                };
            }

            await db.SaveMediaAsync(media with { Status = "Erreur", ErrorMessage = e.Message }, ct);
            report(1, "Erreur · " + Path.GetFileName(path));
        }
    }
}
