using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task SearchClear_ReturnsToTop_AndCompressionDefaultsStaySelectedAfterReopening()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var db = services.GetRequiredService<LibraryDatabase>();
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        var folder = await db.AddFolderAsync(fixture, true);
        for (var i = 0; i < 300; i++)
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(fixture, $"pagination-{i}.jpg"), ThumbnailPath = Path.Combine(fixture, "coffee.png") });
        await vm.Library.LoadAsync();
        var window = Open(vm);
        var gallery = window.GetVisualDescendants().OfType<LibraryView>().Single();
        window.UpdateLayout();
        var scroll = gallery.FindControl<Controls.MediaGallery>("GalleryList")!.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new(0, 1500);
        vm.Library.Query = "pagination-1";
        await vm.Library.SearchCommand.ExecuteAsync(null);
        await UntilAsync(() => scroll.Offset.Y < 1);
        Assert.True(gallery.FindControl<Button>("ClearQueryButton")!.IsVisible);
        await vm.Library.ClearQueryCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Library.Query);
        Assert.False(vm.Library.IsSearch);
        Assert.False(gallery.FindControl<Button>("ClearQueryButton")!.IsVisible);
        for (var i = 0; i < 3; i++)
        {
            vm.ShowCompression([]);
            await vm.Optimizer.PendingFolders;
            vm.Optimizer.ShowAdvanced = true;
            window.UpdateLayout();
            var dialog = window.GetVisualDescendants().OfType<CompressionDialogView>().Single();
            Assert.Equal("JPEG XL", dialog.FindControl<ComboBox>("ImageCodecChoice")!.SelectedItem);
            Assert.Equal("AV1", dialog.FindControl<ComboBox>("VideoCodecChoice")!.SelectedItem);
            dialog.FindControl<ComboBox>("LibraryInputFolder")!.SelectedItem = vm.Optimizer.LibraryFolders.Single(f => f.Id == folder.Id);
            Assert.Equal(fixture, vm.Optimizer.SourceDirectory);
            vm.Optimizer.CancelCommand.Execute(null);
        }
        vm.Stop(); window.Close();
    }

    [AvaloniaFact]
    public async Task Viewer_ArrowsStayFixed_AndClipboardKeepsPhotoAndVideoFileAfterClose()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var paths = services.GetRequiredService<IPlatformPaths>();
        var photoPath = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "coffee.png");
        var card = new MediaCardViewModel(new(new() { FullPath = photoPath }, 0, null, ""));
        var movie = Path.Combine(paths.Data, "start-at-zero.mp4");
        var runner = services.GetRequiredService<ProcessRunner>();
        var tools = services.GetRequiredService<NativeTools>();
        await runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=20", "-t", "4", "-c:v", "libx264", movie]);
        var video = new MediaCardViewModel(new(new() { FullPath = movie, MediaType = MediaType.Video }, 0, 1800, ""));
        var window = Open(vm);
        var viewer = window.GetVisualDescendants().OfType<MediaViewerView>().Single();
        await vm.Viewer.OpenAsync([card, video], card);
        window.UpdateLayout();
        var photoArrow = viewer.FindControl<Button>("NextMediaButton")!.TranslatePoint(default, window);
        viewer.ContextMenu!.Open(viewer);
        var copy = Assert.Single(viewer.ContextMenu.Items.OfType<MenuItem>());
        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        viewer.ContextMenu.Close();
        await Task.Delay(100);
        using (var data = await window.Clipboard!.TryGetDataAsync())
        {
            Assert.NotNull(data);
            Assert.NotNull(await data.TryGetBitmapAsync());
            Assert.Equal(photoPath, (await data.TryGetFilesAsync())!.Single().TryGetLocalPath());
        }
        await vm.Viewer.CloseCommand.ExecuteAsync(null);
        await services.GetRequiredService<ImagePreviewCache>().ClearAsync();
        using (var data = await window.Clipboard!.TryGetDataAsync()) Assert.NotNull(await data!.TryGetBitmapAsync());
        await vm.Viewer.OpenAsync([video, card], video); // Explicit scene offset is respected.
        await UntilAsync(() => vm.Viewer.RenderedVideoFrames > 0);
        Assert.InRange(vm.Viewer.Position, 1400, 2500);
        window.UpdateLayout();
        Assert.Equal(photoArrow, viewer.FindControl<Button>("NextMediaButton")!.TranslatePoint(default, window));
        await vm.Viewer.NextCommand.ExecuteAsync(null);
        await vm.Viewer.NextCommand.ExecuteAsync(null); // Normal navigation must start at zero.
        await UntilAsync(() => vm.Viewer.RenderedVideoFrames > 0);
        await Task.Delay(350);
        Assert.InRange(vm.Viewer.Position, 0, 900);
        await MediaClipboard.CopyAsync(window, movie, vm.Viewer.Image, true);
        using (var data = await window.Clipboard!.TryGetDataAsync())
        {
            Assert.Equal(movie, (await data!.TryGetFilesAsync())!.Single().TryGetLocalPath());
            Assert.Null(await data!.TryGetBitmapAsync());
        }
        await vm.Viewer.CloseCommand.ExecuteAsync(null);
        await window.Clipboard!.ClearAsync();
        vm.Stop(); window.Close();
    }

    [AvaloniaFact]
    public async Task PersonDialog_SelectsReferences_IndexesRealFaces_AndPrioritizesKeywordMatches()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var db = services.GetRequiredService<LibraryDatabase>();
        var people = services.GetRequiredService<PeopleRepository>();
        foreach (var existing in await people.ProfilesAsync()) await people.DeleteAsync(existing.Id);
        var paths = services.GetRequiredService<IPlatformPaths>();
        var source = Path.Combine(paths.Data, "people-library");
        Directory.CreateDirectory(source);
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        var folder = await db.AddFolderAsync(source, true);
        var images = services.GetRequiredService<ImageMagickService>();
        var reference = Path.Combine(fixture, "astronaut.png");
        var samePerson = Path.Combine(source, "holiday-01.jpg");
        var otherPerson = Path.Combine(source, "holiday-02.jpg");
        await images.RunAsync([reference, "-resize", "80%", "-modulate", "95,100,100", samePerson]);
        File.Copy(Path.Combine(fixture, "neil-armstrong.jpg"), otherPerson, true);
        foreach (var path in new[] { samePerson, otherPerson })
        {
            var info = new FileInfo(path);
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = path, SizeBytes = info.Length, LastModifiedAt = info.LastWriteTimeUtc, ThumbnailPath = path });
        }
        await vm.Library.LoadAsync();
        var window = Open(vm);
        var faceIndexer = services.GetRequiredService<FaceLibraryIndexer>();
        await faceIndexer.ScanAsync(new Progress<WorkProgress>(), default);
        Assert.Empty(await people.ProfilesAsync());
        var analysesBeforeFirstPerson = faceIndexer.AnalysisCount;
        vm.Library.PersonCommand.Execute(null);
        Assert.True(vm.IsPersonDialogOpen);
        await vm.People.PendingReferences;
        Assert.Empty(vm.People.Candidates);
        using (var cancel = new CancellationTokenSource())
        {
            var interrupted = vm.People.LoadReferencesAsync([reference], cancel.Token);
            cancel.Cancel();
            vm.People.Prepare([]);
            await vm.People.PendingReferences;
            await interrupted;
            Assert.Empty(vm.People.Candidates);
            Assert.False(vm.People.IsBusy);
        }
        var groupPhoto = Path.Combine(paths.Data, "group-reference.jpg");
        await images.RunAsync([reference, "(", Path.Combine(fixture, "neil-armstrong.jpg"), "-resize", "512x512!", ")", "+append", groupPhoto]);
        await vm.People.LoadReferencesAsync([groupPhoto], default);
        Assert.Equal(2, vm.People.Candidates.Count);
        Assert.All(vm.People.Candidates, c => Assert.False(c.IsSelected));
        vm.People.Candidates[0].IsSelected = true;
        vm.People.Candidates[1].IsSelected = true;
        Assert.False(vm.People.Candidates[0].IsSelected);
        await vm.People.LoadReferencesAsync([reference], default);
        Assert.Single(vm.People.Candidates);
        Assert.True(vm.People.Candidates[0].IsSelected);
        vm.People.Keywords = "Camille, Maman";
        var dialog = window.GetVisualDescendants().OfType<PersonDialogView>().Single();
        Assert.Same(window, TopLevel.GetTopLevel(dialog));
        Assert.True(vm.People.CanAdd);
        Capture(window, "pixseek-add-person");
        await vm.People.AddCommand.ExecuteAsync(null);
        await vm.People.PendingIndex;
        Assert.Equal(analysesBeforeFirstPerson, faceIndexer.AnalysisCount);
        Assert.False(vm.IsPersonDialogOpen);
        vm.Library.Query = "Maman";
        await vm.Library.SearchCommand.ExecuteAsync(null);
        Assert.Equal(samePerson, vm.Library.Cards[0].Media.FullPath);
        var matches = await people.SearchAsync("photos de Camille", "Photos", null, services.GetRequiredService<FaceModelManager>().Version, FaceEmbeddingService.MatchThreshold);
        Assert.Single(matches);
        Assert.Equal(samePerson, matches[0].Media.FullPath);
        var reopened = new PeopleRepository(new LibraryDatabase(paths));
        Assert.Single(await reopened.SearchAsync("Camille", "Photos", folder.Id, services.GetRequiredService<FaceModelManager>().Version, FaceEmbeddingService.MatchThreshold));
        var otherFolder = await db.AddFolderAsync(Path.Combine(fixture), true);
        Assert.Empty(await reopened.SearchAsync("Camille", "Photos", otherFolder.Id, services.GetRequiredService<FaceModelManager>().Version, FaceEmbeddingService.MatchThreshold));
        Assert.Empty(await people.SearchAsync("Camille", "Vidéos", null, services.GetRequiredService<FaceModelManager>().Version, FaceEmbeddingService.MatchThreshold));
        var profile = Assert.Single(await people.ProfilesAsync());
        Assert.Contains(vm.Library.PersonOptions, p => p.Id == profile.Id);
        vm.Library.SelectedPerson = vm.Library.PersonOptions.Single(p => p.Id == profile.Id);
        await vm.Library.PendingFilterRefresh;
        await vm.Library.ClearQueryCommand.ExecuteAsync(null);
        Assert.Equal(samePerson, Assert.Single(vm.Library.Cards).Media.FullPath);
        vm.Library.SelectedType = "Vidéos";
        await vm.Library.PendingFilterRefresh;
        Assert.Empty(vm.Library.Cards);
        await vm.Library.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Library.SelectedPerson!.Id);
        Assert.Equal(2, vm.Library.Cards.Count);
        await people.DeleteAsync(profile.Id);
        Assert.Empty(await people.ProfilesAsync());
        await using (var c = await db.OpenAsync())
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM FaceVector";
            Assert.True((long)(await command.ExecuteScalarAsync())! >= 2);
        }
        Assert.Empty(await people.SearchAsync("Camille", "Photos", null, services.GetRequiredService<FaceModelManager>().Version, FaceEmbeddingService.MatchThreshold));
        vm.Stop(); window.Close();
    }
}
