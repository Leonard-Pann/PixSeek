using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using PixSeek.AI;
using PixSeek.Controls;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using PixSeek.ViewModels;
using PixSeek.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PixSeek.Tests;

public sealed partial class UiTests
{
    private static async Task UntilAsync(Func<bool> condition, int timeoutMs = 12000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(50);
        Assert.True(condition(), "Condition not reached before timeout.");
    }

    [AvaloniaFact]
    public async Task Video_PreservesPhoneAspect_ReachesFullEnd_ReplaysAndSeeksAfterEnd()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var paths = services.GetRequiredService<IPlatformPaths>();
        var runner = services.GetRequiredService<ProcessRunner>();
        var tools = services.GetRequiredService<NativeTools>();
        var probe = services.GetRequiredService<FfprobeService>();
        var raw = Path.Combine(paths.Data, "landscape-source.mp4");
        var rotated = Path.Combine(paths.Data, "phone-rotation.mp4");
        await runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "testsrc2=size=320x180:rate=20", "-t", "3.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", raw]);
        await runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-display_rotation:v:0", "90", "-i", raw, "-c", "copy", rotated]);
        var metadata = await probe.ProbeAsync(rotated);
        Assert.Equal((180, 320), FfprobeService.DisplaySize(metadata));
        var card = new MediaCardViewModel(new(new() { FullPath = rotated, MediaType = MediaType.Video,
            Width = 320, Height = 180, DurationMilliseconds = metadata.DurationMilliseconds }, 0, null, ""));
        var window = Open(vm);
        await vm.Viewer.OpenAsync([card], card);
        Assert.False(vm.Viewer.HasError, vm.Viewer.ErrorMessage);
        await UntilAsync(() => vm.Viewer.RenderedVideoFrames > 0);
        Assert.Equal(180, vm.Viewer.Image!.PixelSize.Width);
        Assert.Equal(320, vm.Viewer.Image.PixelSize.Height);
        Capture(window, "pixseek-phone-video-letterbox");
        await UntilAsync(() => vm.Viewer.HasEnded);
        Assert.False(vm.Viewer.IsPlaying);
        Assert.Equal(vm.Viewer.Duration, vm.Viewer.Position);
        var viewer = window.GetVisualDescendants().OfType<MediaViewerView>().Single();
        Assert.Equal(viewer.FindControl<Slider>("VideoSeek")!.Maximum, viewer.FindControl<Slider>("VideoSeek")!.Value);
        Assert.Equal("00:00:03 / 00:00:03", vm.Viewer.TimeLabel);

        await vm.Viewer.TogglePlaybackCommand.ExecuteAsync(null);
        await UntilAsync(() => vm.Viewer.Position > 200 && vm.Viewer.Position < 2400 && vm.Viewer.IsPlaying);
        Assert.False(vm.Viewer.HasEnded);
        await UntilAsync(() => vm.Viewer.HasEnded);
        vm.Viewer.Position = 1500;
        Assert.StartsWith("00:00:01 /", vm.Viewer.TimeLabel);
        await vm.Viewer.TogglePlaybackCommand.ExecuteAsync(null);
        await UntilAsync(() => vm.Viewer.Position >= 1550 && vm.Viewer.IsPlaying);
        Assert.InRange(vm.Viewer.Position, 1550, 2900);
        Assert.False(vm.Viewer.HasError, vm.Viewer.ErrorMessage);
        Assert.DoesNotContain(viewer.GetVisualDescendants().OfType<Button>(), b => b.Content is "♪" or "🔇");
        await vm.Viewer.CloseCommand.ExecuteAsync(null);
        vm.Stop();
        window.Close();

        var anamorphic = Path.Combine(paths.Data, "anamorphic.mp4");
        await runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-i", raw, "-vf", "setsar=2/1", "-c:v", "libx264", anamorphic]);
        Assert.Equal((640, 180), FfprobeService.DisplaySize(await probe.ProbeAsync(anamorphic)));
    }

    [AvaloniaFact]
    public async Task LargeGallery_AutoFiltersAll3200Records_InfiniteScroll_AndLastRowKeepsCellWidth()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var db = services.GetRequiredService<LibraryDatabase>();
        var paths = services.GetRequiredService<IPlatformPaths>();
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "coffee.png");
        Directory.CreateDirectory(Path.Combine(paths.Data, "large-a"));
        Directory.CreateDirectory(Path.Combine(paths.Data, "large-b"));
        var a = await db.AddFolderAsync(Path.Combine(paths.Data, "large-a"), true);
        var b = await db.AddFolderAsync(Path.Combine(paths.Data, "large-b"), true);
        // SQL fixtures test filtering/pagination independently from file decoding and ONNX quality.
        await Task.Run(async () =>
        {
            for (var i = 0; i < 3200; i++)
            {
                var folder = i % 2 == 0 ? a : b;
                await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(folder.Path, $"media-{i:D4}" + (i % 4 == 0 ? ".mp4" : ".jpg")),
                    MediaType = i % 4 == 0 ? MediaType.Video : MediaType.Image, ThumbnailPath = fixture });
            }
        });
        await vm.Library.LoadAsync();
        var window = Open(vm);
        var gallery = window.GetVisualDescendants().OfType<LibraryView>().Single();
        Assert.DoesNotContain(gallery.GetVisualDescendants().OfType<Button>(), b => b.Content is "← Précédent" or "Page suivante →");
        vm.Library.SelectedTypeChoice = vm.Library.Types.Single(t => t.Id == "Photos");
        await vm.Library.PendingFilterRefresh;
        while (vm.Library.HasMore) await vm.Library.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(2400, vm.Library.Cards.Count);
        Assert.All(vm.Library.Cards, c => Assert.False(c.IsVideo));
        vm.Library.SelectedFolder = vm.Library.FolderOptions.Single(f => f.Id == a.Id);
        await vm.Library.PendingFilterRefresh;
        while (vm.Library.HasMore) await vm.Library.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(800, vm.Library.Cards.Count);
        vm.Library.SelectedFolder = vm.Library.FolderOptions[0];
        vm.Library.SelectedTypeChoice = vm.Library.Types.Single(t => t.Id == "Vidéos");
        await vm.Library.PendingFilterRefresh;
        while (vm.Library.HasMore) await vm.Library.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(800, vm.Library.Cards.Count);
        await vm.Library.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(240, vm.Library.Cards.Count);
        var list = gallery.FindControl<MediaGallery>("GalleryList")!;
        window.UpdateLayout();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new(0, scroll.Extent.Height);
        await UntilAsync(() => vm.Library.Cards.Count > 240);
        Assert.True(list.GetRealizedContainers().Count() < vm.Library.Rows.Count / 2);
        // One incomplete row uses exactly the same number of columns as full rows.
        vm.Library.Reflow(1470);
        Assert.Equal(7, vm.Library.Rows[0].Columns);
        Assert.All(vm.Library.Rows, row => Assert.Equal(7, row.Columns));
        var last = vm.Library.Rows.Last();
        Assert.InRange(last.Cards.Count, 1, 7);
        Assert.True(last.Cards.Count < last.Columns);
        window.UpdateLayout();
        Assert.All(list.GetRealizedContainers().SelectMany(c => c.GetVisualDescendants().OfType<UniformGrid>()), grid => Assert.Equal(7, grid.Columns));
        Capture(window, "pixseek-infinite-gallery");
        vm.Stop();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Navigation_ClearsOldFrameImmediately_EvenWithWarmPhotoCache()
    {
        var vm = await PrepareAsync();
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        var cards = new[] { "coffee.png", "chelsea.png", "astronaut.png" }.Select((name, i) =>
            new MediaCardViewModel(new(new() { Id = i, FullPath = Path.Combine(fixture, name) }, 0, null, ""))).ToArray();
        var window = Open(vm);
        await vm.Viewer.OpenAsync(cards, cards[0]);
        var next = vm.Viewer.NextCommand.ExecuteAsync(null);
        Assert.True(vm.Viewer.IsLoading);
        Assert.Null(vm.Viewer.Image);
        window.UpdateLayout();
        Assert.True(window.GetVisualDescendants().OfType<MediaViewerView>().Single().FindControl<Border>("LoadingSurface")!.IsVisible);
        await next;
        Assert.False(vm.Viewer.HasError, vm.Viewer.ErrorMessage);
        Assert.NotNull(vm.Viewer.Image);
        await vm.Viewer.CloseCommand.ExecuteAsync(null);
        vm.Stop();
        window.Close();
    }
}
