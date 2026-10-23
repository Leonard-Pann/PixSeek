using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using PixSeek.Services;
using PixSeek.ViewModels;
using PixSeek.Views;
using Xunit;

namespace PixSeek.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public async Task CtrlA_TogglesAllFilteredMediaIncludingUnloadedPages_AndPreservesTextSelection()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var db = services.GetRequiredService<LibraryDatabase>();
        var source = Path.Combine(services.GetRequiredService<IPlatformPaths>().Data, "keyboard-selection");
        Directory.CreateDirectory(source);
        var folder = await db.AddFolderAsync(source, true);
        for (var i = 0; i < 1001; i++)
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(source, $"photo-{i}.jpg") });
        await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(source, "video.mp4"), MediaType = MediaType.Video });
        await vm.Library.LoadAsync();
        vm.Library.SelectedType = "Photos";
        await vm.Library.PendingFilterRefresh;
        var window = Open(vm);
        var gallery = window.GetVisualDescendants().OfType<LibraryView>().Single();
        var button = gallery.FindControl<Button>("SelectAllButton")!;
        button.Focus();
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await vm.Library.ToggleAllCommand.ExecutionTask!;
        Assert.Equal(1001, vm.Library.SelectedCount);
        Assert.All(vm.Library.Cards, c => Assert.True(c.IsSelected));
        Assert.True(vm.Library.Cards.Count < vm.Library.SelectedCount,
            $"Expected unmaterialized selections, loaded {vm.Library.Cards.Count}, selected {vm.Library.SelectedCount}.");
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await vm.Library.ToggleAllCommand.ExecutionTask!;
        Assert.Equal(0, vm.Library.SelectedCount);
        vm.Library.Cards[0].IsSelected = true;
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await vm.Library.ToggleAllCommand.ExecutionTask!;
        Assert.Equal(1001, vm.Library.SelectedCount);
        var input = gallery.FindControl<TextBox>("SearchInput")!;
        input.Text = "texte de recherche";
        input.Focus(); input.SelectionStart = input.SelectionEnd = 0;
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        Assert.Equal("texte de recherche", input.SelectedText);
        Assert.Equal(1001, vm.Library.SelectedCount);
        vm.Stop(); window.Close();
    }

    [AvaloniaFact]
    public async Task WarmedVideoRuntime_DecodesBeforeFirstUserClip_AndIsSharedByConcurrentRequests()
    {
        var services = PixSeek.App.Services;
        var paths = services.GetRequiredService<IPlatformPaths>();
        using var runtime = new VideoRuntime(paths, NullLogger<VideoRuntime>.Instance);
        var warmup = runtime.WarmUpAsync();
        Assert.Same(warmup, runtime.WarmUpAsync());
        await warmup.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(runtime.IsWarm);
        Assert.Equal(1, runtime.InitializationCount);
        var movie = Path.Combine(paths.Data, "warm-runtime-test.mp4");
        await services.GetRequiredService<ProcessRunner>().CheckedAsync(services.GetRequiredService<NativeTools>().PathFor("ffmpeg"),
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=20", "-t", "2", "-c:v", "libx264", movie]);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await using (var session = await runtime.CreateSessionAsync(640, 360, default))
        {
            await Task.Run(() => session.Play(movie));
            await UntilAsync(() => session.RenderedFrames > 0, 5000);
            var firstFrameMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(1, runtime.InitializationCount);
            await File.WriteAllTextAsync(Path.Combine(TestEnvironment.FindRoot(), "artifacts", "video-startup-performance.json"),
                System.Text.Json.JsonSerializer.Serialize(new { WarmupMs = runtime.WarmupDuration.TotalMilliseconds, FirstUserFrameMs = firstFrameMs }));
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CreateSessionAsync(640, 360, canceled.Token));
    }
}

public sealed class PersistentThumbnailTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task ReopeningLibrary_ReusesImageAndVideoThumbnails_AndDoesNotInitializeOnnx()
    {
        var source = Path.Combine(e.Work, "persistent-library"); Directory.CreateDirectory(source);
        var photo = Path.Combine(source, "photo.png");
        File.Copy(Path.Combine(e.Root, "tests", "fixtures", "coffee.png"), photo);
        var movie = Path.Combine(source, "video.mp4");
        await e.Runner.CheckedAsync(e.Tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "testsrc2=size=160x90:rate=10", "-t", "1", "-c:v", "libx264", movie]);
        var paths = new FaceEngineTests.FacePaths(e.Paths, Path.Combine(e.Root, "models"));
        var models = new ModelManager(paths);
        await e.Db.ConfigureVectorsAsync(models.Version, models.Manifest.Dimensions);
        var folder = await e.Db.AddFolderAsync(source, true);
        var thumbnails = new ThumbnailService(paths, e.Images, e.Video);
        var timestamps = new Dictionary<string, DateTime>();
        foreach (var path in new[] { photo, movie })
        {
            var info = new FileInfo(path);
            var metadata = path == photo ? await e.Images.IdentifyAsync(path) : await e.Probe.ProbeAsync(path);
            var media = new MediaFile { LibraryFolderId = folder.Id, FullPath = path, MediaType = MediaFormats.TypeOf(path),
                SizeBytes = info.Length, LastModifiedAt = info.LastWriteTimeUtc, Width = metadata.Width, Height = metadata.Height,
                VideoDisplayWidth = path == movie ? 160 : 0, VideoDisplayHeight = path == movie ? 90 : 0,
                DurationMilliseconds = metadata.DurationMilliseconds, IndexedVersion = models.Version, Status = "Indexé" };
            var thumbnail = await thumbnails.CreateAsync(media, default);
            File.SetLastWriteTimeUtc(thumbnail, new DateTime(2001, 2, 3, 0, 0, 0, DateTimeKind.Utc));
            timestamps[thumbnail] = File.GetLastWriteTimeUtc(thumbnail);
            // Simulates an interrupted DB write: recover the existing canonical disk file.
            await e.Db.SaveMediaAsync(media with { ThumbnailPath = path == photo ? "" : thumbnail });
        }
        var reopened = new LibraryDatabase(paths);
        await reopened.InitializeAsync();
        // Native encoders intentionally unavailable: unchanged cached records require no process.
        var unavailable = new PlatformPaths(e.Work, Path.Combine(e.Work, "no-native-tools"));
        var tools = new NativeTools(unavailable, e.Runner);
        var images = new ImageMagickService(tools, e.Runner);
        var videos = new FfmpegService(tools, e.Runner);
        var indexer = new LibraryIndexer(reopened, images, new FfprobeService(tools, e.Runner),
            new ThumbnailService(paths, images, videos), videos, new UnexpectedInference(), models, new SettingsStore(paths),
            NullLogger<LibraryIndexer>.Instance);
        using var control = new JobControl();
        await indexer.ScanAsync([folder], control, new Progress<WorkProgress>());
        var restored = await reopened.MediaAsync(folder: folder.Id);
        Assert.Equal(2, restored.Count);
        foreach (var media in restored)
        {
            Assert.True(timestamps.ContainsKey(media.ThumbnailPath));
            Assert.Equal(timestamps[media.ThumbnailPath], File.GetLastWriteTimeUtc(media.ThumbnailPath));
            Assert.Equal(models.Version, media.IndexedVersion);
        }
    }
    private sealed class UnexpectedInference : ILocalEmbeddingService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Unchanged library initialized ONNX.");
        public Task<float[]> GenerateImageEmbeddingAsync(string imagePath, CancellationToken cancellationToken) => throw new InvalidOperationException("Unchanged image was indexed.");
        public Task<float[]> GenerateTextEmbeddingAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("Unchanged library encoded text.");
    }
}
