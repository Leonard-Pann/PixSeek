using System.Diagnostics;
using System.Threading.Channels;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.Logging;

namespace PixSeek.AI;

public sealed class LibraryIndexer(LibraryDatabase db, ImageMagickService images, FfprobeService probe,
    ThumbnailService thumbnails, FfmpegService videos, ILocalEmbeddingService embeddings,
    ModelManager models, SettingsStore settings, ILogger<LibraryIndexer> logger)
{
    private readonly SemaphoreSlim _singleScan = new(1, 1);
    public event Action? PhotoPrepared;
    private sealed record PlannedMedia(LibraryFolder Folder, string Path, MediaFile? Existing, MediaMetadata? Metadata, int Units);
    private sealed record PlannedFolder(LibraryFolder Folder, IReadOnlyList<PlannedMedia> Items);

    // Calling this service from any context, including Avalonia, never runs discovery/SQLite/native setup on the UI thread.
    public Task ScanAsync(IReadOnlyList<LibraryFolder> folders, JobControl control, IProgress<WorkProgress> progress, bool force = false)
        => Task.Run(() => ScanCoreAsync(folders, control, progress, force), control.Token);

    private async Task ScanCoreAsync(IReadOnlyList<LibraryFolder> folders, JobControl control, IProgress<WorkProgress> progress, bool force)
    {
        var ct = control.Token;
        await _singleScan.WaitAsync(ct);
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            progress.Report(new("Préparation de l'indexation…", IsIndeterminate: true));
            var ai = models.FilesPresent;
            var version = models.Version;
            if (ai)
                await db.ConfigureVectorsAsync(version, models.Manifest.Dimensions, ct, models.Name);
            var excluded = await db.CompressionDestinationsAsync(ct);
            var plans = new List<PlannedFolder>();
            var discovered = 0;
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
                    if (++discovered % 20 == 1)
                        progress.Report(new($"Préparation · {discovered} médias", IsIndeterminate: true));
                    var old = await db.FindAsync(path, ct);
                    var info = new FileInfo(path);
                    var unchanged = IsUnchanged(old, info);
                    var ready = unchanged && !force && old!.Status is not ("Erreur" or "Absent") &&
                        File.Exists(old.ThumbnailPath) && (!ai || old.IndexedVersion == version) &&
                        (old.MediaType != MediaType.Video || old.VideoDisplayWidth > 0);
                    MediaMetadata? metadata = null;
                    var units = ready ? 0 : ai ? 4 : 2;
                    if (!ready && ai && MediaFormats.TypeOf(path) == MediaType.Video)
                    {
                        try { metadata = await probe.ProbeAsync(path, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { logger.LogDebug(e, "Préparation vidéo impossible : {Path}", path); }
                        units = 3 + 2 * Timestamps(metadata?.DurationMilliseconds ?? old?.DurationMilliseconds ?? 0).Count;
                    }
                    items.Add(new(folder, path, old, metadata, units));
                }
                plans.Add(new(folder, items));
                await db.SetFolderStatusAsync(folder.Id, "Analyse en cours", ct);
            }

            var total = plans.Sum(p => p.Items.Sum(i => i.Units) + 1);
            var completed = 0d;
            var completedFiles = 0;
            var reportGate = new object();
            var reporting = Stopwatch.StartNew();
            void Advance(double units, string message, bool fileComplete = false, bool flush = false)
            {
                lock (reportGate)
                {
                    completed += units;
                    if (fileComplete) completedFiles++;
                    // Hundreds of progress callbacks/second otherwise saturate the Avalonia dispatcher.
                    if (!flush && reporting.ElapsedMilliseconds < 100) return;
                    reporting.Restart();
                    progress.Report(new($"{message} · {completedFiles}/{discovered} médias", (int)completed, total,
                        total == 0 ? 1 : Math.Clamp(completed / total, 0, 1)));
                }
            }
            Advance(0, "Indexation", flush: true);
            // Only paths/weights enter this queue. The database is the durable pending-work store.
            // Thumbnails can finish the whole library while a slower ONNX worker consumes this queue.
            var readyForAi = Channel.CreateUnbounded<PlannedMedia>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var aiWorker = Task.Run(async () =>
            {
                try
                {
                    if (!ai) return;
                    await embeddings.InitializeAsync(workers.Token);
                    await foreach (var item in readyForAi.Reader.ReadAllAsync(workers.Token))
                    {
                        await control.CheckpointAsync();
                        var remaining = Math.Max(0, item.Units - 2d);
                        try
                        {
                            var media = await db.FindAsync(item.Path, workers.Token) ?? throw new FileNotFoundException(item.Path);
                            var timestamps = media.MediaType == MediaType.Video ? Timestamps(media.DurationMilliseconds) : [];
                            var stages = media.MediaType == MediaType.Video ? timestamps.Count * 2 + 1 : 2;
                            var unit = remaining / stages;
                            var vectors = new List<FrameVector>();
                            if (media.MediaType == MediaType.Image)
                            {
                                vectors.Add(new(0, media.ThumbnailPath, await embeddings.GenerateImageEmbeddingAsync(item.Path, workers.Token)));
                                Advance(unit, "Analyse IA · " + media.FileName);
                                remaining -= unit;
                            }
                            else
                            {
                                for (var index = 0; index < timestamps.Count; index++)
                                {
                                    await control.CheckpointAsync();
                                    var timestamp = timestamps[index];
                                    var frame = thumbnails.CachePath(media, "frame-" + timestamp);
                                    if (!File.Exists(frame))
                                    {
                                        var temp = frame + ".partial.jpg";
                                        try
                                        {
                                            await videos.ExtractFrameAsync(item.Path, timestamp, temp, 640, workers.Token);
                                            File.Move(temp, frame, true);
                                        }
                                        finally { if (File.Exists(temp)) File.Delete(temp); }
                                    }
                                    var label = $"{media.FileName} · image {index + 1}/{timestamps.Count}";
                                    Advance(unit, "Extraction · " + label);
                                    remaining -= unit;
                                    vectors.Add(new(timestamp, frame, await embeddings.GenerateImageEmbeddingAsync(frame, workers.Token)));
                                    Advance(unit, "Analyse IA · " + label);
                                    remaining -= unit;
                                }
                            }
                            if (!IsUnchanged(media, new FileInfo(item.Path)))
                                throw new IOException("Le fichier a changé pendant l'analyse ; il sera repris au prochain scan.");
                            await db.SaveVectorsAsync(media, version, vectors, workers.Token);
                            await db.SaveMediaAsync(media with { Status = "Indexé", IndexedVersion = version }, workers.Token);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { await SaveErrorAsync(item, e, workers.Token); }
                        Advance(remaining, "Indexation", fileComplete: true, flush: true);
                    }
                }
                catch { workers.Cancel(); throw; }
            }, workers.Token);
            var thumbnailWorker = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(plans.SelectMany(p => p.Items),
                        new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 3), CancellationToken = workers.Token },
                        async (item, token) =>
                        {
                            await control.CheckpointAsync();
                            if (item.Units == 0)
                            {
                                Advance(0, "Déjà indexé", fileComplete: true);
                                return;
                            }
                            try
                            {
                                var info = new FileInfo(item.Path);
                                var unchanged = IsUnchanged(item.Existing, info);
                                if (item.Existing != null && !unchanged)
                                    await db.InvalidateVectorsAsync(item.Existing.Id, token);
                                var media = unchanged ? item.Existing! : new MediaFile
                                {
                                    LibraryFolderId = item.Folder.Id, FullPath = info.FullName, SizeBytes = info.Length,
                                    LastModifiedAt = info.LastWriteTimeUtc, MediaType = MediaFormats.TypeOf(item.Path)
                                };
                                var metadata = item.Metadata ?? (media.MediaType == MediaType.Image
                                    ? await images.IdentifyAsync(item.Path, token) : await probe.ProbeAsync(item.Path, token));
                                var display = media.MediaType == MediaType.Video ? FfprobeService.DisplaySize(metadata) : (0, 0);
                                media = await db.SaveMediaAsync(media with
                                {
                                    Width = metadata.Width, Height = metadata.Height, DurationMilliseconds = metadata.DurationMilliseconds,
                                    Codec = metadata.Codec, HasAlpha = metadata.HasAlpha, FrameCount = metadata.FrameCount,
                                    VideoDisplayWidth = display.Item1, VideoDisplayHeight = display.Item2,
                                    Status = "Miniature en cours", ErrorMessage = null
                                }, token);
                                var thumb = await thumbnails.CreateAsync(media, token);
                                await db.SaveMediaAsync(media with { ThumbnailPath = thumb, Status = "Miniature prête" }, token);
                                if (media.MediaType == MediaType.Image) PhotoPrepared?.Invoke();
                                Advance(2, "Miniature · " + media.FileName);
                                if (ai && (force || !unchanged || media.IndexedVersion != version))
                                    await readyForAi.Writer.WriteAsync(item, token);
                                else
                                    Advance(Math.Max(0, item.Units - 2), "Miniature", fileComplete: true);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception e)
                            {
                                await SaveErrorAsync(item, e, token);
                                Advance(item.Units, "Erreur · " + Path.GetFileName(item.Path), fileComplete: true);
                            }
                        });
                    readyForAi.Writer.TryComplete();
                }
                catch (Exception e) { readyForAi.Writer.TryComplete(e); workers.Cancel(); throw; }
            }, workers.Token);
            await Task.WhenAll(thumbnailWorker, aiWorker);

            foreach (var plan in plans)
            {
                await control.CheckpointAsync();
                if (!Directory.Exists(plan.Folder.Path)) continue; // A disconnected drive is never treated as deleted.
                var seen = plan.Items.Select(i => i.Path).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                var missing = new List<MediaFile>();
                for (var offset = 0; ; offset += 240)
                {
                    var page = await db.MediaAsync(offset, 240, folder: plan.Folder.Id, ct: ct);
                    if (page.Count == 0) break;
                    missing.AddRange(page.Where(m => !seen.Contains(m.FullPath) &&
                        (!File.Exists(m.FullPath) || (!plan.Folder.Recursive && Path.GetDirectoryName(m.FullPath) != plan.Folder.Path))));
                }
                foreach (var media in missing)
                {
                    await db.InvalidateVectorsAsync(media.Id, ct);
                    await db.SaveMediaAsync(media with { Status = "Absent", IndexedVersion = null, ErrorMessage = "Fichier absent ou hors du périmètre du scan." }, ct);
                }
                await db.SetFolderStatusAsync(plan.Folder.Id, "Prêt", ct);
                Advance(1, "Bibliothèque mise à jour", flush: true);
            }
            progress.Report(new(ai ? "Analyse et indexation terminées" : "Miniatures prêtes · Téléchargez le modèle pour activer la recherche", total, total, 1));
        }
        catch
        {
            foreach (var folder in folders.Where(f => Directory.Exists(f.Path)))
                await db.SetFolderStatusAsync(folder.Id, "Interrompu");
            throw;
        }
        finally { _singleScan.Release(); }
    }

    private IReadOnlyList<long> Timestamps(long duration) => FfmpegService.FrameTimestamps(duration,
        settings.Current.FrameIntervalSeconds, settings.Current.MaxVideoFrames);
    private static bool IsUnchanged(MediaFile? old, FileInfo info) => old != null && info.Exists &&
        old.SizeBytes == info.Length && old.LastModifiedAt == info.LastWriteTimeUtc;
    private async Task SaveErrorAsync(PlannedMedia item, Exception error, CancellationToken ct)
    {
        logger.LogWarning(error, "Analyse impossible : {Path}", item.Path);
        var media = await db.FindAsync(item.Path, ct) ?? new MediaFile
        {
            LibraryFolderId = item.Folder.Id, FullPath = item.Path, MediaType = MediaFormats.TypeOf(item.Path)
        };
        await db.SaveMediaAsync(media with { Status = "Erreur", ErrorMessage = error.Message }, ct);
    }
}
