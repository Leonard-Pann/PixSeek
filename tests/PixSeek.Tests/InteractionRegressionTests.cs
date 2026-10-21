using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using PixSeek.Services;
using PixSeek.ViewModels;
using PixSeek.Views;
using SkiaSharp;
using Xunit;

namespace PixSeek.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public async Task HoverHighlightsOnlyOneCard_AndMainWindowHasAnIcon()
    {
        var vm = await PrepareAsync();
        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        var folder = await db.AddFolderAsync(fixture, true);
        for (var i = 0; i < 12; i++)
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(fixture, $"hover-{i}.jpg"), ThumbnailPath = Path.Combine(fixture, "coffee.png") });
        await vm.Library.LoadAsync();
        var window = Open(vm);
        window.UpdateLayout();
        var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("mediaCard")).ToArray();
        Assert.True(cards.Length >= 2);
        var first = cards[0];
        window.MouseMove(first.TranslatePoint(new Point(first.Bounds.Width / 2, 100), window)!.Value);
        window.UpdateLayout();
        Assert.True(first.IsPointerOver);
        Assert.False(cards[1].IsPointerOver);
        Assert.NotEqual(first.BorderBrush, cards[1].BorderBrush);
        var row = first.GetVisualAncestors().OfType<ListBoxItem>().First();
        Assert.NotNull(row.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "GalleryRowContent"));
        Assert.DoesNotContain(row.GetVisualDescendants().OfType<ContentPresenter>(), p => p.TemplatedParent == row && p.Name == "PART_ContentPresenter");
        Capture(window, "pixseek-hover-card");
        var native = new MainWindow();
        Assert.NotNull(native.Icon);
        native.Close();
        vm.Stop(); window.Close();
    }

    [AvaloniaFact]
    public async Task PreviewPrefetch_DoesNotBlockCachedOrForegroundPhotos_AndDuplicateLoadsShareDecode()
    {
        var services = PixSeek.App.Services;
        var paths = services.GetRequiredService<IPlatformPaths>();
        var images = services.GetRequiredService<ImageMagickService>();
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        var slow = Path.Combine(fixture, "astronaut.png");
        var warm = Path.Combine(fixture, "coffee.png");
        var cold = Path.Combine(fixture, "chelsea.png");
        using var decoder = new GatedPreviewDecoder(slow);
        using var cache = new ImagePreviewCache(paths, images, decoder);
        using var warmLease = await cache.LoadAsync(warm, default);
        var prefetch = cache.PrefetchAsync(slow, default);
        await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var cached = await cache.LoadAsync(warm, default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(warmLease.Bitmap, cached.Bitmap);
            using var foreground = await cache.LoadAsync(cold, default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotSame(cached.Bitmap, foreground.Bitmap);
            Assert.False(prefetch.IsCompleted);
            // A foreground request for the same file joins the single existing decode.
            var sameFile = cache.LoadAsync(slow, default);
            decoder.Release.Set();
            await prefetch;
            using var shared = await sameFile;
            Assert.Equal(3, cache.DecodeCount);
            await cache.ClearAsync();
            using var encoded = new MemoryStream();
            shared.Bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
            Assert.True(encoded.Length > 100);
        }
        finally { decoder.Release.Set(); await prefetch; }
    }

    [AvaloniaFact]
    public async Task PreviewClearDuringDecode_DoesNotRepopulateCache_AndHonorsCanceledWaiters()
    {
        var services = PixSeek.App.Services;
        var photo = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "astronaut.png");
        using var decoder = new GatedPreviewDecoder(photo);
        using var cache = new ImagePreviewCache(services.GetRequiredService<IPlatformPaths>(), services.GetRequiredService<ImageMagickService>(), decoder);
        using var cancel = new CancellationTokenSource();
        var canceled = cache.LoadAsync(photo, cancel.Token);
        await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retained = cache.LoadAsync(photo, default);
        await Task.Delay(50);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        await cache.ClearAsync();
        decoder.Release.Set();
        using var lease = await retained;
        using var next = await cache.LoadAsync(photo, default);
        Assert.NotSame(lease.Bitmap, next.Bitmap);
        Assert.Equal(2, cache.DecodeCount);
    }

    [AvaloniaFact]
    public void PreviewDecoder_ActuallyCapsPngDimensions()
    {
        var paths = PixSeek.App.Services.GetRequiredService<IPlatformPaths>();
        var path = Path.Combine(paths.Data, "large-preview.png");
        using (var bitmap = new SKBitmap(5000, 1300))
        {
            bitmap.Erase(new SKColor(101, 87, 221));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var output = File.Create(path);
            data.SaveTo(output);
        }
        using var decoded = new ImagePreviewDecoder().Decode(path);
        Assert.Equal(4096, decoded.PixelSize.Width);
        Assert.InRange(decoded.PixelSize.Height, 1064, 1065);
    }

    [AvaloniaFact]
    public async Task OpeningBenchmark_RealLargePhotos_AndVideoReuseStoredGeometryAndRuntime()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var paths = services.GetRequiredService<IPlatformPaths>();
        var images = services.GetRequiredService<ImageMagickService>();
        var source = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "astronaut.png");
        var results = new List<object>();
        var window = Open(vm);
        var cache = services.GetRequiredService<ImagePreviewCache>();
        foreach (var extension in new[] { ".jpg", ".png", ".webp", ".jxl" })
        {
            var path = Path.Combine(paths.Data, "opening-benchmark" + extension);
            await images.RunAsync([source, "-resize", "4000x3000!", "-quality", "88", path]);
            var card = new MediaCardViewModel(new(new() { FullPath = path }, 0, null, ""));
            await cache.ClearAsync();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await vm.Viewer.OpenAsync([card], card);
            var cold = timer.Elapsed.TotalMilliseconds;
            Assert.False(vm.Viewer.HasError, vm.Viewer.ErrorMessage);
            var bitmap = vm.Viewer.Image;
            Assert.NotNull(bitmap);
            await vm.Viewer.CloseCommand.ExecuteAsync(null);
            timer.Restart();
            await vm.Viewer.OpenAsync([card], card);
            var warm = timer.Elapsed.TotalMilliseconds;
            Assert.Same(bitmap, vm.Viewer.Image);
            results.Add(new { Format = extension, Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height, ColdMs = cold, CachedMs = warm });
            await vm.Viewer.CloseCommand.ExecuteAsync(null);
        }
        var runner = services.GetRequiredService<ProcessRunner>();
        var tools = services.GetRequiredService<NativeTools>();
        var movie = Path.Combine(paths.Data, "opening-benchmark.mp4");
        await runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "testsrc2=size=640x360:rate=20", "-t", "3", "-c:v", "libx264", movie]);
        var info = new FileInfo(movie);
        var clip = new MediaCardViewModel(new(new() { FullPath = movie, MediaType = MediaType.Video,
            SizeBytes = info.Length, LastModifiedAt = info.LastWriteTimeUtc,
            VideoDisplayWidth = 640, VideoDisplayHeight = 360, DurationMilliseconds = 3000 }, 0, null, ""));
        var runtime = services.GetRequiredService<VideoRuntime>();
        var initializations = runtime.InitializationCount;
        var probes = vm.Viewer.VideoProbeCount;
        for (var i = 0; i < 2; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await vm.Viewer.OpenAsync([clip], clip);
            await UntilAsync(() => vm.Viewer.RenderedVideoFrames > 0);
            results.Add(new { Format = "H.264", Pass = i, FirstFrameMs = timer.Elapsed.TotalMilliseconds });
            Assert.InRange(vm.Viewer.Position, 0, 900);
            await vm.Viewer.CloseCommand.ExecuteAsync(null);
        }
        Assert.Equal(probes, vm.Viewer.VideoProbeCount);
        Assert.InRange(runtime.InitializationCount, initializations, initializations + 1);
        await File.WriteAllTextAsync(Path.Combine(TestEnvironment.FindRoot(), "artifacts", "opening-performance-0.3.1.json"),
            System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        vm.Stop(); window.Close();
    }
    private sealed class GatedPreviewDecoder(string gatedPath) : IImagePreviewDecoder, IDisposable
    {
        private readonly ImagePreviewDecoder _real = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public Bitmap Decode(string path)
        {
            if (path == gatedPath)
            {
                Started.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test did not release the image decoder.");
            }
            return _real.Decode(path);
        }
        public void Dispose() => Release.Dispose();
    }
}
