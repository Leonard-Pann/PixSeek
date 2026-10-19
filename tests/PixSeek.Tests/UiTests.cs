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

public sealed class UiTests
{
    private static async Task<MainViewModel> PrepareAsync()
    {
        var vm = PixSeek.App.Services.GetRequiredService<MainViewModel>();
        await vm.InitializeAsync();
        vm.Optimizer.CancelCommand.Execute(null);
        var db = PixSeek.App.Services.GetRequiredService<LibraryDatabase>();
        foreach (var folder in await db.FoldersAsync())
            await db.RemoveFolderAsync(folder.Id);
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
        gallery.FindControl<ComboBox>("MediaTypeFilter")!.SelectedItem = "Photos";
        await vm.Library.PendingFilterRefresh;
        Assert.Equal(2, vm.Library.Cards.Count);
        gallery.FindControl<CalendarDatePicker>("FromDateFilter")!.SelectedDate = new DateTime(2025, 2, 20);
        await vm.Library.PendingFilterRefresh;
        Assert.Single(vm.Library.Cards);
        Assert.Equal("recent.jpg", vm.Library.Cards[0].FileName);
        gallery.FindControl<CalendarDatePicker>("ToDateFilter")!.SelectedDate = new DateTime(2025, 2, 20);
        await vm.Library.PendingFilterRefresh;
        Assert.Single(vm.Library.Cards);
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
}
