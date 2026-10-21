using PixSeek.Core;
using PixSeek.Infrastructure;
using Microsoft.Extensions.Logging;

namespace PixSeek.AI;

public sealed class FaceLibraryIndexer(LibraryDatabase db, PeopleRepository people, FaceEmbeddingService engine, ILogger<FaceLibraryIndexer> logger)
{
    private readonly SemaphoreSlim _single = new(1);
    public int AnalysisCount { get; private set; }
    public async Task ScanAsync(IProgress<WorkProgress> progress, CancellationToken ct)
    {
        await _single.WaitAsync(ct);
        try
        {
            await people.InitializeAsync(ct);
            await people.PruneAsync(ct);
            for (var offset = 0; ; offset += 120)
            {
                var media = await db.MediaAsync(offset, 120, "Photos", ct: ct);
                if (media.Count == 0) break;
                foreach (var item in media)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await people.IsScannedAsync(item, engine.Version, ct)) continue;
                    if (!File.Exists(item.FullPath)) continue;
                    try
                    {
                        var faces = await engine.DetectAsync(item.FullPath, ct);
                        AnalysisCount++;
                        var info = new FileInfo(item.FullPath);
                        if (!info.Exists || info.Length != item.SizeBytes || info.LastWriteTimeUtc != item.LastModifiedAt) continue;
                        await people.SaveFacesAsync(item, faces, engine.Version, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "Face analysis failed: {Path}", item.FullPath);
                        await people.SaveFacesAsync(item, [], engine.Version, ct, error.Message);
                    }
                    progress.Report(new(item.FileName, offset + 1));
                }
            }
        }
        finally { _single.Release(); }
    }
}
