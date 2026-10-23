using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PixSeek.Core;
using PixSeek.Mobile;
using Xunit;

namespace PixSeek.Tests;
public sealed class MobileUiTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [AvaloniaFact]
    public async Task PhoneLayout_IsVirtualized_HasNoCompression_AndOffersNativeImportChoices()
    {
        Application.Current!.Styles.Add(new MobileTheme());
        var platform = new TestMobilePlatform(e);
        using var model = new MobileViewModel(platform);
        await model.InitializeAsync();
        model.Library.Stop(); await model.Library.Completion;
        Assert.True(string.IsNullOrEmpty(model.Error), model.Error);
        var folder = await model.Database.AddContentSourceAsync("pixseek://test", "Camera", true);
        for (var i = 0; i < 360; i++)
            await model.Database.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = "content://test/images/" + i,
                DisplayName = "image " + i + ".jpg", ThumbnailPath = Path.Combine(e.Root, "tests", "fixtures", "coffee.png") });
        await model.RefreshAsync();
        var shell = new MobileShell { DataContext = model };
        var window = new Window { Width = 390, Height = 844, Content = shell };
        window.Show(); window.UpdateLayout();
        await Task.Delay(150);
        var list = shell.FindControl<ListBox>("Gallery")!;
        Assert.InRange(list.GetRealizedContainers().Count(), 1, model.Rows.Count - 1);
        Assert.All(model.Rows.SelectMany(r => r.Cells), c => Assert.InRange(c.Width, 100, 150));
        Assert.DoesNotContain(shell.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString()?.Contains("Compress", StringComparison.OrdinalIgnoreCase) == true);
        model.OpenAddCommand.Execute(null); window.UpdateLayout();
        foreach (var key in new[] { "DeviceGallery", "DeviceAlbums", "PickMedia", "BrowseFolder" })
            Assert.Contains(shell.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.Text[key]);
        Capture(window, Path.Combine(e.Root, "artifacts", "android", "phone-import.png"));
        model.ClosePanelCommand.Execute(null);
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Capture(window, Path.Combine(e.Root, "artifacts", "android", "phone-gallery.png"));
        model.OpenPersonCommand.Execute(null); window.UpdateLayout();
        Assert.True(model.PersonOpen);
        Assert.Contains(shell.GetVisualDescendants().OfType<TextBox>(), t => t.PlaceholderText == model.Text["PersonNames"]);
        model.ClosePanelCommand.Execute(null); window.Close();
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap); Directory.CreateDirectory(Path.GetDirectoryName(path)!); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
    private sealed class TestMobilePlatform(TestEnvironment e) : IMobilePlatform
    {
        public IPlatformPaths Paths { get; } = new FaceEngineTests.FacePaths(e.Paths, Path.Combine(e.Root, "artifacts", "android", "assets", "models"));
        public Task<IReadOnlyList<DeviceAlbum>> AlbumsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeviceAlbum>>([]);
        public Task<MobileSource?> PickTreeAsync(CancellationToken ct) => Task.FromResult<MobileSource?>(null);
        public Task<IReadOnlyList<string>> PickPhotosAsync(bool photosOnly, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DeviceMedia>> EnumerateAsync(MobileSource source, bool recursive, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeviceMedia>>([]);
        public Task<DeviceMedia> DescribeAsync(string uri, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ThumbnailAsync(MediaFile media, CancellationToken ct) => Task.FromResult(media.ThumbnailPath);
        public Task<string> VideoFrameAsync(MediaFile media, long milliseconds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Bitmap> PreviewAsync(MediaFile media, CancellationToken ct) => Task.FromResult(new Bitmap(media.ThumbnailPath));
        public Control CreateVideoPlayer(MediaFile media, long timestamp, Action<string> error) => throw new NotSupportedException();
        public Task<byte[]> ReadModelRgbAsync(string file, CancellationToken ct, bool squash = false) => e.Images.ReadModelRgbAsync(file, ct, squash);
        public Task<byte[]> ReadFaceRgbAsync(string file, int size, CancellationToken ct) => e.Images.ReadFaceRgbAsync(file, size, ct);
        public Task WriteFaceThumbnailAsync(byte[] rgb, int size, string destination, CancellationToken ct) => e.Images.WriteFaceThumbnailAsync(rgb, size, destination, ct);
        public void StopVideo() { }
        public void HideKeyboard() { }
        public void Share(MediaFile media) => throw new NotSupportedException();
        public void KeepScreenOn(bool enabled) { }
    }
}
