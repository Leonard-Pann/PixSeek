using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PixSeek.Controls;
using PixSeek.Core;
using PixSeek.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using PixSeek.ViewModels;
using PixSeek.Views;
using PixSeek.Localization;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PixSeek.Tests.UiTestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace PixSeek.Tests;
public static class UiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var root = TestEnvironment.FindRoot();
        Environment.SetEnvironmentVariable("PIXSEEK_DATA_DIR", Path.Combine(root, "artifacts", "ui-smoke", Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("PIXSEEK_MODEL_DIR", Path.Combine(root, "artifacts", "models"));
        return AppBuilder.Configure<PixSeek.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont();
    }
}

public sealed partial class UiTests
{
    private static async Task<MainViewModel> PrepareAsync()
    {
        var vm = PixSeek.App.Services.GetRequiredService<MainViewModel>();
        await vm.InitializeAsync();
        vm.Optimizer.CancelCommand.Execute(null);
        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        foreach (var folder in await db.FoldersAsync())
            await db.RemoveFolderAsync(folder.Id);
        vm.Settings.Language = vm.Settings.Languages.Single(l => l.Id == "fr");
        await vm.Settings.PendingSave;
        vm.Library.Query = "";
        await vm.Library.ClearFiltersCommand.ExecuteAsync(null);
        vm.PageIndex = 1;
        await vm.Library.LoadAsync();
        return vm;
    }

    private static Window Open(MainViewModel vm)
    {
        var window = new Window
        {
            Width = 1320,
            Height = 900,
            DataContext = vm,
            Content = new MainView()
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(TestEnvironment.FindRoot(), "artifacts", name + ".png"), PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public async Task RealViews_Render_Navigate_AndVirtualize()
    {
        var vm = await PrepareAsync();
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.Equal(1, vm.PageIndex);
        var window = Open(vm);
        var gallery = window.GetVisualDescendants().OfType<LibraryView>().Single();
        Assert.DoesNotContain(gallery.GetVisualDescendants().OfType<Button>(), b => b.Content is "Appliquer" or "Photothèque");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Votre photothèque");
        foreach (var page in new[]
        {
            1,
            2,
            4
        }

        )
        {
            vm.PageIndex = page;
            Capture(window, $"pixseek-page-{page}");
        }

        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        var folder = await db.AddFolderAsync(fixture, true);
        // Synthetic records exercise viewport realization only; semantic tests below use the actual files.
        for (var i = 0; i < 240; i++)
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(fixture, $"virtualization-{i}.png"), ThumbnailPath = Path.Combine(fixture, "coffee.png"), Width = 320, Height = 240 });
        await vm.Library.LoadAsync();
        vm.PageIndex = 1;
        window.UpdateLayout();
        await Task.Delay(250);
        var list = gallery.FindControl<MediaGallery>("GalleryList")!;
        Assert.InRange(list.GetRealizedContainers().Count(), 1, vm.Library.Rows.Count - 1);
        var row = list.GetRealizedContainers().First();
        var point = row.TranslatePoint(new Point(100, row.Bounds.Height - 3), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(-1, list.SelectedIndex);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Capture(window, "pixseek-gallery-dark");
        Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
        await vm.RescanCommand.ExecuteAsync(null);
        vm.Library.Query = "un chat";
        await vm.Library.SearchCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Library.Cards);
        Assert.Equal("chelsea.png", vm.Library.Cards[0].FileName);
        await Task.Delay(100);
        Capture(window, "pixseek-real-french-search");
        vm.Stop();
        window.Close();
    }

    [AvaloniaFact]
    public async Task FiltersApplyAutomatically_AndBackgroundRefreshPreservesCardsRowsAndThumbnails()
    {
        var vm = await PrepareAsync();
        var paths = PixSeek.App.Services.GetRequiredService<IPlatformPaths>();
        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        var source = Path.Combine(paths.Data, "filter-test");
        Directory.CreateDirectory(source);
        var folder = await db.AddFolderAsync(source, true);
        var thumb = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "coffee.png");
        await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(source, "old.jpg"), MediaType = MediaType.Image, ThumbnailPath = thumb, LastModifiedAt = new DateTime(2024, 1, 10, 12, 0, 0, DateTimeKind.Utc) });
        await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(source, "recent.jpg"), MediaType = MediaType.Image, ThumbnailPath = thumb, LastModifiedAt = new DateTime(2025, 2, 20, 12, 0, 0, DateTimeKind.Utc) });
        await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(source, "video.mp4"), MediaType = MediaType.Video, ThumbnailPath = thumb, LastModifiedAt = new DateTime(2025, 2, 21, 12, 0, 0, DateTimeKind.Utc) });
        await vm.Library.LoadAsync();
        var window = Open(vm);
        await Task.Delay(100);
        var row = vm.Library.Rows[0];
        var card = vm.Library.Cards[0];
        card.IsSelected = true;
        var gallery = window.GetVisualDescendants().OfType<LibraryView>().Single();
        var thumbnail = gallery.GetVisualDescendants().OfType<AsyncThumbnail>().First();
        var resets = 0;
        vm.Library.Rows.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                resets++;
        };
        for (var i = 0; i < 3; i++)
            await vm.Library.RefreshInBackgroundAsync();
        window.UpdateLayout();
        Assert.Same(row, vm.Library.Rows[0]);
        Assert.Same(card, vm.Library.Cards[0]);
        Assert.True(card.IsSelected);
        Assert.Equal(0, resets);
        Assert.Same(thumbnail, gallery.GetVisualDescendants().OfType<AsyncThumbnail>().First());
        Assert.False(vm.Library.IsLoading);
        gallery.FindControl<ComboBox>("MediaTypeFilter")!.SelectedItem = vm.Library.Types.Single(t => t.Id == "Photos");
        await vm.Library.PendingFilterRefresh;
        Assert.Equal(2, vm.Library.Cards.Count);
        Assert.Empty(gallery.GetVisualDescendants().OfType<CalendarDatePicker>());
        var folderFilter = gallery.FindControl<ComboBox>("FolderFilter")!;
        folderFilter.SelectedItem = vm.Library.FolderOptions.Single(f => f.Id == folder.Id);
        await vm.Library.PendingFilterRefresh;
        Assert.Equal(2, vm.Library.Cards.Count);
        folderFilter.SelectedItem = vm.Library.FolderOptions[0];
        await vm.Library.PendingFilterRefresh;
        Assert.Equal(0, vm.Library.SelectedFolder!.Id);
        await vm.Library.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Library.Cards.Count);
        vm.Stop();
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompressionIsAnOverlay_ForFoldersAndContextSelection_WithWorkingQualityControls()
    {
        var vm = await PrepareAsync();
        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        var folder = await db.AddFolderAsync(fixture, true);
        foreach (var name in new[]
        {
            "chelsea.png",
            "coffee.png"
        }

        )
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(fixture, name), ThumbnailPath = Path.Combine(fixture, name) });
        await vm.Library.LoadAsync();
        var window = Open(vm);
        await Task.Delay(100);
        vm.Library.CompressCommand.Execute(null);
        Assert.True(vm.IsCompressionDialogOpen);
        Assert.True(vm.Optimizer.IsFolderInput);
        Assert.Equal(1, vm.PageIndex);
        var dialog = window.GetVisualDescendants().OfType<CompressionDialogView>().Single();
        Assert.Same(window, TopLevel.GetTopLevel(dialog));
        Capture(window, "pixseek-compression-folder");
        vm.Optimizer.CancelCommand.Execute(null);
        Assert.False(vm.IsCompressionDialogOpen);
        foreach (var card in vm.Library.Cards)
            card.IsSelected = true;
        var selected = vm.Library.Cards[0];
        var button = window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.DataContext, selected) && b.ContextMenu != null);
        button.ContextMenu!.Open(button);
        var compress = button.ContextMenu.Items.OfType<MenuItem>().Single(item => (string? )item.Header == "Compresser");
        compress.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        button.ContextMenu.Close();
        Assert.True(vm.IsCompressionDialogOpen);
        Assert.False(vm.Optimizer.IsFolderInput);
        Assert.Contains("2 média", vm.Optimizer.Scope);
        Assert.Same(window, TopLevel.GetTopLevel(dialog));
        vm.Optimizer.PresetIndex = 0;
        Assert.Equal(24, vm.Optimizer.CurrentOptions.Crf);
        vm.Optimizer.PresetIndex = 2;
        Assert.Equal(38, vm.Optimizer.CurrentOptions.Crf);
        vm.Optimizer.ShowAdvanced = true;
        Assert.Equal("JPEG XL", vm.Optimizer.ImageCodec);
        Assert.Equal("AV1", vm.Optimizer.VideoCodec);
        window.UpdateLayout();
        dialog.FindControl<Slider>("ImageQualitySlider")!.Value = 95;
        dialog.FindControl<Slider>("VideoQualitySlider")!.Value = 86;
        Assert.InRange(vm.Optimizer.CurrentOptions.Distance!.Value, .54, .56);
        Assert.Equal(24, vm.Optimizer.CurrentOptions.Crf);
        vm.Optimizer.VideoCodec = "Conserver";
        Assert.Equal("Conserver", vm.Optimizer.CurrentOptions.VideoCodec);
        Capture(window, "pixseek-compression-selection-advanced");
        var destination = Path.Combine(PixSeek.App.Services.GetRequiredService<IPlatformPaths>().Data, "export de sélection");
        vm.Optimizer.Destination = destination;
        var original = await File.ReadAllBytesAsync(selected.Media.FullPath);
        await vm.Optimizer.ValidateCommand.ExecuteAsync(null);
        Assert.True(vm.Optimizer.HasResults, vm.Optimizer.ValidationError);
        Assert.Equal(2, vm.Optimizer.Results.Count);
        Assert.All(vm.Optimizer.Results, r => Assert.Equal("Converti", r.Status));
        Assert.Equal(2, Directory.GetFiles(destination, "*.jxl").Length);
        Assert.Equal(original, await File.ReadAllBytesAsync(selected.Media.FullPath));
        vm.Optimizer.CancelCommand.Execute(null);
        Assert.False(vm.IsCompressionDialogOpen);
        vm.Stop();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Viewer_IsEmbedded_ZoomsAtCursor_CachesPhotos_AndPlaysVideoWithSeek()
    {
        var vm = await PrepareAsync();
        var services = PixSeek.App.Services;
        var paths = services.GetRequiredService<IPlatformPaths>();
        var db = services.GetRequiredService<LibraryDatabase>();
        var source = Path.Combine(paths.Data, "viewer-test");
        Directory.CreateDirectory(source);
        var fixture = Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures");
        foreach (var name in new[]
        {
            "coffee.png",
            "chelsea.png"
        }

        )
            File.Copy(Path.Combine(fixture, name), Path.Combine(source, name), true);
        var folder = await db.AddFolderAsync(source, true);
        foreach (var name in new[]
        {
            "coffee.png",
            "chelsea.png"
        }

        )
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(source, name), ThumbnailPath = Path.Combine(fixture, name) });
        await vm.Library.LoadAsync();
        var window = Open(vm);
        var cache = services.GetRequiredService<PixSeek.Services.ImagePreviewCache>();
        await cache.ClearAsync();
        var card = vm.Library.Cards[0];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await vm.Viewer.OpenAsync(vm.Library.Cards, card);
        var coldMs = watch.Elapsed.TotalMilliseconds;
        window.UpdateLayout();
        await Task.Delay(60);
        Assert.True(vm.Viewer.IsOpen);
        Assert.False(vm.Viewer.HasError, vm.Viewer.ErrorMessage);
        var viewer = window.GetVisualDescendants().OfType<MediaViewerView>().Single();
        Assert.Same(window, TopLevel.GetTopLevel(viewer));
        var photo = viewer.FindControl<ZoomImage>("PhotoSurface")!;
        var anchor = new Point(700, 350);
        var world = (anchor - new Point(photo.Translation.X, photo.Translation.Y)) / photo.Scale;
        window.MouseWheel(photo.TranslatePoint(anchor, window)!.Value, new Vector(0, 2));
        var after = (anchor - new Point(photo.Translation.X, photo.Translation.Y)) / photo.Scale;
        Assert.InRange(Math.Abs(after.X - world.X) + Math.Abs(after.Y - world.Y), 0, .001);
        Assert.True(photo.Scale > 1);
        Capture(window, "pixseek-viewer-photo");
        var originalBitmap = vm.Viewer.Image;
        await vm.Viewer.CloseCommand.ExecuteAsync(null);
        watch.Restart();
        await vm.Viewer.OpenAsync(vm.Library.Cards, card);
        var cachedMs = watch.Elapsed.TotalMilliseconds;
        Assert.Same(originalBitmap, vm.Viewer.Image);
        Assert.True(cache.CacheHits > 0);
        await Task.Delay(40);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await vm.Viewer.PendingLoad;
        Assert.NotSame(originalBitmap, vm.Viewer.Image);
        await vm.Viewer.CloseCommand.ExecuteAsync(null);
        var movie = Path.Combine(source, "video test.mp4");
        var runner = services.GetRequiredService<PixSeek.Media.ProcessRunner>();
        var tools = services.GetRequiredService<PixSeek.Media.NativeTools>();
        await runner.CheckedAsync(tools.PathFor("ffmpeg"), ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "4", "-c:v", "libx264", "-c:a", "aac", movie]);
        var video = new MediaCardViewModel(new(new() { Id = 999, FullPath = movie, MediaType = MediaType.Video, Width = 320, Height = 180, DurationMilliseconds = 4000 }, .7, 1000, ""));
        await vm.Viewer.OpenAsync([video, card], video);
        Assert.False(vm.Viewer.HasError, vm.Viewer.ErrorMessage);
        await Task.Delay(1200);
        window.UpdateLayout();
        Assert.True(vm.Viewer.IsVideo);
        Assert.True(vm.Viewer.Position >= 1000);
        Assert.True(vm.Viewer.RenderedVideoFrames > 0);
        vm.Viewer.TogglePlaybackCommand.Execute(null);
        await Task.Delay(150);
        Assert.False(vm.Viewer.IsPlaying);
        vm.Viewer.Position = 2200;
        vm.Viewer.Volume = 25;
        vm.Viewer.Muted = true;
        await Task.Delay(150);
        Assert.InRange(vm.Viewer.Position, 1800, 2600);
        Capture(window, "pixseek-viewer-video");
        viewer.FindControl<Slider>("VideoSeek")!.Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await vm.Viewer.PendingLoad;
        Assert.False(vm.Viewer.IsVideo);
        await vm.Viewer.CloseCommand.ExecuteAsync(null);
        await File.WriteAllTextAsync(Path.Combine(TestEnvironment.FindRoot(), "artifacts", "preview-performance.json"), System.Text.Json.JsonSerializer.Serialize(new { coldMs, cachedMs }));
        vm.Stop();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_TranslateLive_Autosave_AndLibraryMonitorsAdditionsAndDeletions()
    {
        var vm = await PrepareAsync();
        var window = Open(vm);
        vm.PageIndex = 4;
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content is "Modifier" or "Rescanner / reprendre" or "Enregistrer");
        var settingsView = window.GetVisualDescendants().OfType<SettingsView>().Single();
        foreach (var language in vm.Settings.Languages)
        {
            vm.Settings.Language = language;
            await vm.Settings.PendingSave;
            window.UpdateLayout();
            Assert.Contains(settingsView.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == TranslationService.Text("General"));
        }

        vm.Settings.Language = vm.Settings.Languages.Single(l => l.Id == "en");
        await vm.Settings.PendingSave;
        Capture(window, "pixseek-settings-en");
        var store = PixSeek.App.Services.GetRequiredService<SettingsStore>();
        Assert.Equal("en", store.Current.Language);
        vm.Settings.Theme = vm.Settings.Themes.Single(t => t.Id == "Sombre");
        await vm.Settings.PendingSave;
        Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
        var paths = PixSeek.App.Services.GetRequiredService<IPlatformPaths>();
        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        var source = Path.Combine(paths.Data, "watch-test");
        Directory.CreateDirectory(source);
        await db.AddFolderAsync(source, true);
        await vm.CheckForLibraryChangesAsync();
        var added = Path.Combine(source, "new photo.png");
        File.Copy(Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "coffee.png"), added);
        var timeout = DateTime.UtcNow.AddSeconds(20);
        while ((await db.FindAsync(added))?.IndexedVersion == null && DateTime.UtcNow < timeout)
            await Task.Delay(100);
        Assert.NotNull((await db.FindAsync(added))?.IndexedVersion);
        var indexed = (await db.FindAsync(added))!;
        await vm.Settings.ClearCachesCommand.ExecuteAsync(null);
        Assert.True(File.Exists((await db.FindAsync(added))!.ThumbnailPath));
        Assert.Equal(indexed.IndexedVersion, (await db.FindAsync(added))!.IndexedVersion);
        Assert.True(File.Exists(added));
        File.Delete(added);
        timeout = DateTime.UtcNow.AddSeconds(10);
        while ((await db.FindAsync(added))?.Status != "Absent" && DateTime.UtcNow < timeout)
            await Task.Delay(100);
        Assert.Equal("Absent", (await db.FindAsync(added))?.Status);
        await using (var c = await db.OpenAsync())
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM MediaEmbedding";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }

        vm.Stop();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Models_ActivateExclusively_RebuildVectors_AndPersistChoice()
    {
        var vm = await PrepareAsync();
        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        var paths = PixSeek.App.Services.GetRequiredService<IPlatformPaths>();
        var source = Path.Combine(paths.Data, "model-switch-library");
        Directory.CreateDirectory(source);
        for (var i = 0; i < 24; i++)
            File.Copy(Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "coffee.png"), Path.Combine(source, $"photo-{i}.png"), true);
        var folder = await db.AddFolderAsync(source, true);
        var siglip = vm.Settings.Models.Single(m => m.Id == "siglip2");
        Assert.True(siglip.IsInstalled);
        var activating = vm.Settings.ActivateModelCommand.ExecuteAsync(siglip);
        Assert.True(siglip.IsActive);
        Assert.True(siglip.CanSelect);
        Assert.Single(vm.Settings.Models, m => m.IsActive);
        await activating;
        await UntilAsync(() => vm.IsIndexing);
        Assert.False(vm.Settings.IsModelBusy);
        Assert.All(vm.Settings.Models, m => Assert.True(m.CanSelect));
        vm.PauseIndexCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.True(siglip.IsActive);
        Assert.Single(vm.Settings.Models, m => m.IsActive);
        var store = PixSeek.App.Services.GetRequiredService<SettingsStore>();
        Assert.Equal("siglip2", store.Current.ActiveModelId);
        var xlmr = vm.Settings.Models.Single(m => m.Id == "xlmr");
        var switchingBack = vm.Settings.ActivateModelCommand.ExecuteAsync(xlmr);
        Assert.True(xlmr.IsActive);
        Assert.False(siglip.IsActive);
        await switchingBack;
        Assert.True(xlmr.IsActive);
        Assert.False(siglip.IsActive);
        Assert.Equal("xlmr", store.Current.ActiveModelId);
        await UntilAsync(() => vm.IsIndexing);
        await UntilAsync(() => !vm.IsIndexing && !vm.Settings.IsModelBusy, timeoutMs: 30000);
        await vm.RemoveFolderCommand.ExecuteAsync(folder);
        vm.Stop();
    }
}
