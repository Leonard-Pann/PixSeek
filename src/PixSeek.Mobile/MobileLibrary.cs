using System.Text.Json;
using System.Threading.Channels;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;

namespace PixSeek.Mobile;

/// <summary>Foreground mobile pipeline. Two independent consumers keep thumbnails and face work
/// responsive while the semantic encoder is running. Persistent signatures resume interrupted work.</summary>
public sealed class MobileLibrary(IMobilePlatform platform, LibraryDatabase db, PeopleRepository people,
    ModelManager models, MobileEmbeddingService embeddings, FaceEmbeddingService faces)
{
    private readonly SemaphoreSlim _scan = new(1), _sourcesGate = new(1);
    private readonly JobControl _control = new();
    private CancellationTokenSource? _lifetime;
    private Task _running = Task.CompletedTask;
    private bool _refreshRequested;
    public event Action<WorkProgress>? Progress;
    public event Action? Changed;
    public event Action<string>? Error;
    public bool IsBusy { get; private set; }
    public bool IsPaused { get; private set; }
    public int FrameInterval { get; set; } = 10;
    public int MaxFrames { get; set; } = 24;
    private string SourcesFile => Path.Combine(platform.Paths.Data, "sources.json");

    public IReadOnlyList<MobileSource> Sources()
    {
        try { return JsonSerializer.Deserialize<MobileSource[]>(File.ReadAllText(SourcesFile)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }
    public async Task AddAsync(MobileSource source, CancellationToken ct)
    {
        await _sourcesGate.WaitAsync(ct);
        try
        {
            var sources = Sources().ToList();
            var previous = sources.FindIndex(s => s.Uri == source.Uri);
            if (previous >= 0 && source.Kind == "selection")
                sources[previous] = source with { Items = (sources[previous].Items ?? []).Concat(source.Items ?? []).Distinct().ToArray() };
            else if (previous >= 0) sources[previous] = source;
            else sources.Add(source);
            await SaveSourcesAsync(sources, ct);
        }
        finally { _sourcesGate.Release(); }
        await db.AddContentSourceAsync(source.Uri, source.Name, true, ct);
        QueueScan();
    }
    public async Task RemoveAsync(LibraryFolder folder, CancellationToken ct)
    {
        await _scan.WaitAsync(ct);
        try
        {
            await _sourcesGate.WaitAsync(ct);
            try { await SaveSourcesAsync(Sources().Where(s => s.Uri != folder.Path).ToArray(), ct); }
            finally { _sourcesGate.Release(); }
            await db.RemoveContentSourceAsync(folder.Id, ct);
            await people.PruneAsync(ct);
            Changed?.Invoke();
        }
        finally { _scan.Release(); }
    }
    private async Task SaveSourcesAsync(IEnumerable<MobileSource> sources, CancellationToken ct)
    {
        var temp = SourcesFile + ".partial";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(sources), ct);
        File.Move(temp, SourcesFile, true);
    }
    public void Start()
    {
        if (_lifetime is { IsCancellationRequested: false }) { QueueScan(); return; }
        _lifetime?.Dispose(); _lifetime = new();
        _running = Task.Run(() => MonitorAsync(_lifetime.Token));
    }
    public void QueueScan() { _refreshRequested = true; }
    public void TogglePause() { IsPaused = !IsPaused; if (IsPaused) _control.Pause(); else _control.Resume(); }
    public void Stop() { _lifetime?.Cancel(); _control.Resume(); IsPaused = false; }
    public Task Completion => _running;
    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            await db.InitializeAsync(ct);
            await db.ConfigureVectorsAsync(models.Version, models.Manifest.Dimensions, ct, "XLM-R + ViT-B/32 · int8");
            await people.InitializeAsync(ct);
            _refreshRequested = true;
            var lastScan = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                if (_refreshRequested || DateTime.UtcNow - lastScan >= TimeSpan.FromMinutes(1))
                {
                    _refreshRequested = false;
                    try { await ScanAsync(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e) { Error?.Invoke(e.Message); }
                    lastScan = DateTime.UtcNow;
                }
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error?.Invoke(e.Message); }
    }
    private async Task ScanAsync(CancellationToken ct)
    {
        await _scan.WaitAsync(ct);
        IsBusy = true;
        try
        {
            var work = new List<MediaFile>();
            var folders = await db.FoldersAsync(ct);
            foreach (var source in Sources())
            {
                await _control.WaitAsync(ct);
                var folder = folders.FirstOrDefault(f => f.Path == source.Uri) ?? await db.AddContentSourceAsync(source.Uri, source.Name, true, ct);
                try
                {
                    var found = await platform.EnumerateAsync(source, folder.Recursive, ct);
                    var present = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in found)
                    {
                        ct.ThrowIfCancellationRequested();
                        present.Add(item.Uri);
                        var old = await db.FindAsync(item.Uri, ct);
                        var same = old != null && old.SizeBytes == item.Size && old.LastModifiedAt == item.Modified && old.Status != "Absent";
                        if (same)
                        {
                            await db.LinkContentMediaAsync(old!.Id, folder.Id, ct);
                            work.Add(old); continue;
                        }
                        if (old != null) await db.InvalidateVectorsAsync(old.Id, ct);
                        var media = new MediaFile { FullPath = item.Uri, DisplayName = item.Name, LibraryFolderId = folder.Id, SizeBytes = item.Size,
                            MediaType = item.Type, LastModifiedAt = item.Modified, Width = item.Width, Height = item.Height,
                            DurationMilliseconds = item.Duration, Status = "Découvert" };
                        var saved = await db.SaveMediaAsync(media, ct);
                        await db.LinkContentMediaAsync(saved.Id, folder.Id, ct);
                        work.Add(saved);
                    }
                    await db.ReconcileContentSourceAsync(folder.Id, present, ct);
                    await db.SetFolderStatusAsync(folder.Id, "Prêt", ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    await db.SetFolderStatusAsync(folder.Id, e.Message, ct);
                    Error?.Invoke(source.Name + ": " + e.Message);
                }
            }
            work = work.DistinctBy(m => m.Id).ToList();
            Changed?.Invoke();
            var thumbnails = Channel.CreateBounded<MediaFile>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
            var recognition = Channel.CreateBounded<MediaFile>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
            var thumbnailTask = Task.Run(async () =>
            {
                await foreach (var media in thumbnails.Reader.ReadAllAsync(ct))
                {
                    await _control.WaitAsync(ct);
                    try
                    {
                        if (File.Exists(media.ThumbnailPath)) continue;
                        var path = await platform.ThumbnailAsync(media, ct);
                        await db.UpdateThumbnailAsync(media.Id, path, ct);
                        Changed?.Invoke();
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { Error?.Invoke(media.FileName + ": " + e.Message); }
                }
            }, ct);
            var faceTask = Task.Run(async () =>
            {
                await foreach (var media in recognition.Reader.ReadAllAsync(ct))
                {
                    await _control.WaitAsync(ct);
                    if (media.MediaType != MediaType.Image || await people.IsScannedAsync(media, faces.Version, ct)) continue;
                    try { await people.SaveFacesAsync(media, await faces.DetectAsync(media.FullPath, ct), faces.Version, ct); }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        await people.SaveFacesAsync(media, [], faces.Version, ct, e.Message);
                        Error?.Invoke(media.FileName + ": " + e.Message);
                    }
                }
            }, ct);
            var completed = 0;
            // Queue producers run independently from inference to prevent a slow face scan from
            // delaying gallery thumbnails. Both queues are bounded; originals are never copied.
            var produce = Task.Run(async () =>
            {
                try { foreach (var media in work) await thumbnails.Writer.WriteAsync(media, ct); }
                finally { thumbnails.Writer.TryComplete(); }
            }, ct);
            var produceFaces = Task.Run(async () =>
            {
                try { foreach (var media in work) await recognition.Writer.WriteAsync(media, ct); }
                finally { recognition.Writer.TryComplete(); }
            }, ct);
            try
            {
                foreach (var media in work)
                {
                    ct.ThrowIfCancellationRequested();
                    await _control.WaitAsync(ct);
                    Progress?.Invoke(new(media.FileName, completed, work.Count, work.Count == 0 ? 0 : completed / (double)work.Count));
                    if (media.IndexedVersion != models.Version)
                    {
                        try
                        {
                            var vectors = new List<FrameVector>();
                            if (media.MediaType == MediaType.Image)
                                vectors.Add(new(0, media.ThumbnailPath, await embeddings.GenerateImageEmbeddingAsync(media.FullPath, ct)));
                            else
                                foreach (var timestamp in VideoSampling.Timestamps(media.DurationMilliseconds, FrameInterval, MaxFrames))
                                {
                                    var frame = await platform.VideoFrameAsync(media, timestamp, ct);
                                    vectors.Add(new(timestamp, frame, await embeddings.GenerateImageEmbeddingAsync(frame, ct)));
                                }
                            await db.SaveVectorsAsync(media, models.Version, vectors, ct);
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { Error?.Invoke(media.FileName + ": " + e.Message); }
                    }
                    completed++;
                    Progress?.Invoke(new(media.FileName, completed, work.Count, completed / (double)Math.Max(1, work.Count)));
                }
            }
            finally { await Task.WhenAll(produce, produceFaces, thumbnailTask, faceTask); }
            await people.PruneAsync(ct);
            Changed?.Invoke();
            Progress?.Invoke(new("", completed, work.Count, 1));
        }
        finally { IsBusy = false; _scan.Release(); }
    }
}
