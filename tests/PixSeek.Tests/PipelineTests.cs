using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PixSeek.Tests;
public sealed class PipelineTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task ThumbnailWorkers_ContinueWhileRealOnnxInitializationIsBlocked()
    {
        var root = Path.Combine(e.Work, "parallel-library");
        Directory.CreateDirectory(root);
        for (var i = 0; i < 24; i++)
            File.Copy(Path.Combine(e.Root, "tests", "fixtures", "coffee.png"), Path.Combine(root, $"photo-{i}.png"));
        var paths = new ModelPaths(e.Paths, Path.Combine(e.Root, "artifacts", "models"));
        var manager = new ModelManager(paths);
        using var real = new OnnxEmbeddingService(manager, e.Images, new CpuExecutionProviderFactory());
        var gate = new GatedOnnx(real);
        var indexer = new LibraryIndexer(e.Db, e.Images, e.Probe, new(paths, e.Images, e.Video), e.Video,
            gate, manager, new SettingsStore(paths), NullLogger<LibraryIndexer>.Instance);
        var folder = await e.Db.AddFolderAsync(root, true);
        using var control = new JobControl();
        var scan = indexer.ScanAsync([folder], control, new Progress<WorkProgress>());
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = Environment.TickCount64 + 20000;
            IReadOnlyList<MediaFile> media;
            do
            {
                await Task.Delay(100);
                media = await e.Db.MediaAsync(folder: folder.Id);
            } while ((media.Count != 24 || media.Any(m => !File.Exists(m.ThumbnailPath))) && Environment.TickCount64 < deadline);
            Assert.Equal(24, media.Count);
            Assert.All(media, m => Assert.True(File.Exists(m.ThumbnailPath)));
            Assert.False(scan.IsCompleted);
            Assert.All(media, m => Assert.Null(m.IndexedVersion));
        }
        finally { gate.Release.TrySetResult(); }
        await scan;
        Assert.All(await e.Db.MediaAsync(folder: folder.Id), m => Assert.NotNull(m.IndexedVersion));
        await e.Db.RemoveFolderAsync(folder.Id);
    }

    private sealed class GatedOnnx(OnnxEmbeddingService real) : ILocalEmbeddingService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await real.InitializeAsync(cancellationToken);
        }
        public Task<float[]> GenerateImageEmbeddingAsync(string imagePath, CancellationToken cancellationToken)
            => real.GenerateImageEmbeddingAsync(imagePath, cancellationToken);
        public Task<float[]> GenerateTextEmbeddingAsync(string text, CancellationToken cancellationToken)
            => real.GenerateTextEmbeddingAsync(text, cancellationToken);
    }

    [Fact]
    public async Task FullIndexPipeline_ExtractsVideoFrames_PersistsAndReusesEmbeddings_RecoversUnavailableFolder()
    {
        var root = Path.Combine(e.Work, "library");
        Directory.CreateDirectory(root);
        var cat = Path.Combine(root, "chat.png");
        var astronaut = Path.Combine(root, "astronaute.png");
        File.Copy(Path.Combine(e.Root, "tests", "fixtures", "chelsea.png"), cat);
        File.Copy(Path.Combine(e.Root, "tests", "fixtures", "astronaut.png"), astronaut);
        await File.WriteAllTextAsync(Path.Combine(root, "corrompu.jpg"), "corrupt");
        var movie = Path.Combine(root, "deux scènes.mp4");
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-loop", "1", "-t", "1.5", "-i", cat, "-loop", "1", "-t", "1.5", "-i", astronaut, "-filter_complex", "[0:v]scale=320:240,setsar=1[a];[1:v]scale=320:240,setsar=1[b];[a][b]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-r", "10", "-c:v", "libx264", "-pix_fmt", "yuv420p", movie]);
        var paths = new ModelPaths(e.Paths, Path.Combine(e.Root, "artifacts", "models"));
        var manager = new ModelManager(paths);
        using var embedding = new OnnxEmbeddingService(manager, e.Images, new CpuExecutionProviderFactory());
        var settings = new SettingsStore(paths);
        await settings.SaveAsync(settings.Current with { FrameIntervalSeconds = 1, MaxVideoFrames = 10 });
        var indexer = new LibraryIndexer(e.Db, e.Images, e.Probe, new ThumbnailService(paths, e.Images, e.Video), e.Video, embedding, manager, settings, NullLogger<LibraryIndexer>.Instance);
        var folder = await e.Db.AddFolderAsync(root, true);
        var progress = new List<WorkProgress>();
        using (var control = new JobControl())
            await indexer.ScanAsync([folder], control, new InlineProgress(p => progress.Add(p)));
        var determined = progress.Where(p => !p.IsIndeterminate).ToArray();
        Assert.Single(determined.Select(p => p.Total).Distinct());
        Assert.All(determined.Zip(determined.Skip(1)), pair => Assert.True(pair.First.Fraction <= pair.Second.Fraction, "La progression ne doit jamais reculer."));
        Assert.Equal(1, determined[^1].Fraction);
        Assert.Contains(progress, p => p.Message.Contains("image 2/", StringComparison.Ordinal) && p.Fraction is> 0 and < 1);
        var stats = await e.Db.StatsAsync();
        Assert.Equal(3, stats.Indexed);
        Assert.Equal(1, stats.Pending);
        var bad = await e.Db.FindAsync(Path.Combine(root, "corrompu.jpg"));
        Assert.Equal("Erreur", bad!.Status);
        await using (var c = await e.Db.OpenAsync())
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM VideoFrame";
            Assert.InRange((long)(await command.ExecuteScalarAsync())!, 3, 10);
        }

        var results = await e.Db.SearchAsync(await embedding.GenerateTextEmbeddingAsync("un astronaute", default), 10, default);
        Assert.Contains(results, r => r.Media.FullPath == movie && r.TimestampMilliseconds >= 1000);
        var before = await e.Db.FindAsync(cat);
        var cacheModified = File.GetLastWriteTimeUtc(before!.ThumbnailPath);
        long maxId;
        await using (var c = await e.Db.OpenAsync())
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT MAX(Id) FROM MediaEmbedding";
            maxId = (long)(await command.ExecuteScalarAsync())!;
        }

        using (var control = new JobControl())
            await indexer.ScanAsync([folder], control, new Progress<WorkProgress>());
        await using (var c = await e.Db.OpenAsync())
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT MAX(Id) FROM MediaEmbedding";
            Assert.Equal(maxId, (long)(await command.ExecuteScalarAsync())!);
        }

        Assert.Equal(cacheModified, File.GetLastWriteTimeUtc(before.ThumbnailPath));
        // A new database instance represents reopening the application.
        var reopened = new LibraryDatabase(paths);
        await reopened.InitializeAsync();
        await reopened.ConfigureVectorsAsync(manager.Version, 512);
        Assert.Equal(3, (await reopened.StatsAsync()).Indexed);
        var unavailable = folder with
        {
            Path = Path.Combine(e.Work, "disconnected-drive")
        };
        using (var control = new JobControl())
            await indexer.ScanAsync([unavailable], control, new Progress<WorkProgress>());
        Assert.Equal(3, (await e.Db.StatsAsync()).Indexed);
        Assert.Equal("Indisponible", (await e.Db.FoldersAsync()).Single().Status);
        await e.Images.RunAsync(["-size", "100x100", "xc:blue", cat]);
        using (var control = new JobControl())
            await indexer.ScanAsync([folder], control, new Progress<WorkProgress>());
        Assert.Equal(100, (await e.Db.FindAsync(cat))!.Width);
    }

    private sealed class InlineProgress(Action<WorkProgress> action) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress value) => action(value);
    }

    private sealed class ModelPaths(IPlatformPaths p, string models) : IPlatformPaths
    {
        public string Data => p.Data;
        public string Cache => p.Cache;
        public string Models => models;
        public string Logs => p.Logs;
        public string Native => p.Native;
    }
}
