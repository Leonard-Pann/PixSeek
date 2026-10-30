using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PixSeek.Controls;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Localization;
using PixSeek.Services;
using PixSeek.ViewModels;
using PixSeek.Views;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public async Task FastStartup_DisplaysCachedPhoto_BeforeStartingCodecProbes()
    {
        await using var services = CreateStartupServices();
        await SeedStartupLibraryAsync(services);
        var vm = services.GetRequiredService<MainViewModel>();
        var window = Open(vm);
        var realViewportWait = vm.WaitForInitialGalleryAsync!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double viewportMs = -1;
        vm.WaitForInitialGalleryAsync = async token =>
        {
            await realViewportWait(token);
            Assert.Null(services.GetRequiredService<NativeTools>().Capabilities);
            Assert.True(vm.ToolPreparation.IsCompleted);
            viewportMs = clock.Elapsed.TotalMilliseconds;
        };
        try
        {
            await vm.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(vm.IsPreparingLibrary);
            Assert.True(viewportMs >= 0);
            await File.WriteAllTextAsync(Path.Combine(TestEnvironment.FindRoot(), "artifacts", "startup-first-viewport.json"),
                System.Text.Json.JsonSerializer.Serialize(new { FirstCachedPhotoViewportMs = viewportMs,
                    Scope = "Headless InitializeAsync, isolated library with one public cached photo; excludes process launch" }));
            vm.ShowCompression([]);
            Assert.True(vm.IsCompressionDialogOpen);
            Assert.False(vm.IsPreparingLibrary);
            vm.Optimizer.CancelCommand.Execute(null);
            await vm.PendingCompressionOpen.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(vm.IsCompressionDialogOpen); // A late probe must not reopen a canceled panel.
        }
        finally { await vm.ShutdownAsync(); window.Close(); }
    }

    private static ServiceProvider CreateStartupServices()
    {
        var previous = Environment.GetEnvironmentVariable("PIXSEEK_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PIXSEEK_DATA_DIR", Path.Combine(TestEnvironment.FindRoot(),
                "artifacts", "startup-ui", Guid.NewGuid().ToString("N")));
            var services = AppServices.Create();
            _ = services.GetRequiredService<IPlatformPaths>();
            return services;
        }
        finally { Environment.SetEnvironmentVariable("PIXSEEK_DATA_DIR", previous); }
    }

    private static async Task SeedStartupLibraryAsync(ServiceProvider services, int count = 1, string? thumbnail = null)
    {
        var db = services.GetRequiredService<LibraryDatabase>();
        await db.InitializeAsync();
        // An unavailable directory must retain its records, without touching any personal media.
        var source = Path.Combine(services.GetRequiredService<IPlatformPaths>().Data, "unavailable");
        Directory.CreateDirectory(source);
        var folder = await db.AddFolderAsync(source, true);
        Directory.Delete(source); // Empty test directory: represents a disconnected source.
        for (var i = 0; i < count; i++)
            await db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(source, $"photo-{i}.jpg"),
                ThumbnailPath = thumbnail ?? Path.Combine(TestEnvironment.FindRoot(), "tests", "fixtures", "coffee.png") });
    }

    [AvaloniaFact]
    public async Task InitialLoader_RendersWhileInitializationAwaitsViewport_AndDoesNotReturnOnRefresh()
    {
        await using var services = CreateStartupServices();
        await SeedStartupLibraryAsync(services, 300);
        var vm = services.GetRequiredService<MainViewModel>();
        var window = Open(vm);
        try
        {
            var overlay = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "InitialLoadingOverlay");
            Assert.True(overlay.IsVisible);
            Assert.False(vm.IsLibraryInteractive);
            var viewportWait = vm.WaitForInitialGalleryAsync!;
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            vm.WaitForInitialGalleryAsync = async token =>
            {
                Assert.Null(services.GetRequiredService<NativeTools>().Capabilities);
                Assert.True(vm.ToolPreparation.IsCompleted); // No codec probes started before the first viewport.
                entered.SetResult();
                await release.Task.WaitAsync(token);
                await viewportWait(token);
            };
            var initialization = vm.InitializeAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(initialization.IsCompleted);
            Assert.False(vm.Library.IsLoading);
            Assert.NotEmpty(vm.Library.Cards);
            Assert.True(overlay.IsVisible); // SQL completion alone must not dismiss startup.
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = theme;
                Capture(window, "pixseek-startup-" + theme.Key);
                Assert.Equal(theme, overlay.ActualThemeVariant);
            }
            var uiTick = false;
            await Dispatcher.UIThread.InvokeAsync(() => uiTick = true, DispatcherPriority.Background);
            Assert.True(uiTick); // Initialization leaves the UI dispatcher free to render/animate.
            release.SetResult();
            await initialization.WaitAsync(TimeSpan.FromSeconds(15));
            window.UpdateLayout();
            Assert.False(vm.IsPreparingLibrary);
            Assert.False(overlay.IsVisible);
            Assert.True(vm.IsLibraryInteractive);
            var gallery = window.GetVisualDescendants().OfType<MediaGallery>().Single();
            // Screenshot rendering under full-suite load can consume the bounded viewport wait.
            await UntilAsync(() => gallery.GetVisualDescendants().OfType<AsyncThumbnail>().Any(t => t.Source != null), 10000);
            Assert.Contains(gallery.GetVisualDescendants().OfType<AsyncThumbnail>(), t => t.Source != null);
            Assert.True(gallery.GetVisualDescendants().OfType<AsyncThumbnail>().Count() < vm.Library.Cards.Count);
            var becameVisible = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsPreparingLibrary) && vm.IsPreparingLibrary) becameVisible++; };
            await vm.Library.RefreshInBackgroundAsync();
            await vm.Library.LoadAsync();
            Assert.Equal(0, becameVisible);
            Assert.False(overlay.IsVisible);
        }
        finally { await vm.ShutdownAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task InitialLoader_EmptyLibrary_DoesNotWaitForThumbnails()
    {
        await using var services = CreateStartupServices();
        var vm = services.GetRequiredService<MainViewModel>();
        var window = Open(vm);
        try
        {
            vm.WaitForInitialGalleryAsync = _ => throw new InvalidOperationException("Empty library requested thumbnails.");
            await vm.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(vm.IsPreparingLibrary);
            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Empty(vm.Library.Cards);
            Assert.Equal(TranslationService.Text("EmptyLibrary"), vm.Library.Summary);
        }
        finally { await vm.ShutdownAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task InitialLoader_DatabaseError_DismissesOverlayAndExposesError()
    {
        await using var services = CreateStartupServices();
        var db = services.GetRequiredService<LibraryDatabase>();
        Directory.CreateDirectory(db.DatabasePath); // Real SQLite open failure in isolated test data.
        var vm = services.GetRequiredService<MainViewModel>();
        var window = Open(vm);
        try
        {
            await vm.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(vm.IsPreparingLibrary);
            Assert.True(vm.HasError);
            Assert.True(vm.IsLibraryInteractive);
        }
        finally { await vm.ShutdownAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task InitialLoader_MissingOrCorruptThumbnails_SettlesOnPlaceholders()
    {
        foreach (var kind in new[] { "missing.jpg", "corrupt.jpg", "" })
        {
            await using var services = CreateStartupServices();
            var path = kind.Length == 0 ? "" : Path.Combine(services.GetRequiredService<IPlatformPaths>().Data, kind);
            if (kind == "corrupt.jpg") await File.WriteAllTextAsync(path, "Invalid thumbnail");
            await SeedStartupLibraryAsync(services, thumbnail: path);
            var vm = services.GetRequiredService<MainViewModel>();
            var window = Open(vm);
            try
            {
                await vm.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
                window.UpdateLayout();
                Assert.False(vm.IsPreparingLibrary);
                var thumbnail = window.GetVisualDescendants().OfType<AsyncThumbnail>().First();
                Assert.True(thumbnail.IsLoadComplete);
                Assert.Null(thumbnail.Source);
                Assert.False(vm.HasError, vm.ErrorMessage);
            }
            finally { await vm.ShutdownAsync(); window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task InitialLoader_UnresponsiveViewport_HasABoundedWait()
    {
        await using var services = CreateStartupServices();
        await SeedStartupLibraryAsync(services);
        var vm = services.GetRequiredService<MainViewModel>();
        var entered = new TaskCompletionSource();
        var canceled = false;
        vm.WaitForInitialGalleryAsync = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { canceled = token.IsCancellationRequested; }
        };
        try
        {
            var initialization = vm.InitializeAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await initialization.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.True(canceled);
            Assert.False(vm.IsPreparingLibrary);
            Assert.False(vm.HasError, vm.ErrorMessage);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [AvaloniaFact]
    public async Task InitialLoader_ShutdownCancelsPendingViewport_AndDoesNotStartIndexing()
    {
        await using var services = CreateStartupServices();
        await SeedStartupLibraryAsync(services);
        var vm = services.GetRequiredService<MainViewModel>();
        var entered = new TaskCompletionSource();
        vm.WaitForInitialGalleryAsync = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var initialization = vm.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await vm.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsPreparingLibrary);
        Assert.False(vm.IsIndexing);
        Assert.False(vm.People.IsIndexing);
        Assert.False(vm.HasError, vm.ErrorMessage);
    }
}
