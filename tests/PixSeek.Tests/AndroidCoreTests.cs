using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Mobile;
using Xunit;

namespace PixSeek.Tests;

public sealed class AndroidCoreTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task ContentUris_PreserveDisplayNames_AndReconcileWithoutDeletingOriginals()
    {
        var folder = await e.Db.AddContentSourceAsync("content://com.android.externalstorage.documents/tree/primary%3APictures", "Pictures", true);
        var media = await e.Db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = "content://media/external/images/media/42", DisplayName = "été à la plage.jpg", MediaType = MediaType.Image });
        await e.Db.LinkContentMediaAsync(media.Id, folder.Id, default);
        Assert.Equal("été à la plage.jpg", media.FileName); Assert.Equal(".jpg", media.Extension);
        await e.Db.ConfigureVectorsAsync("mobile-test", 3);
        await e.Db.SaveVectorsAsync(media, "mobile-test", [new(0, "", [1, 0, 0])], default);
        await e.Db.UpdateThumbnailAsync(media.Id, "thumbnail.jpg", default);
        var restored = await e.Db.FindAsync(media.FullPath);
        Assert.Equal("mobile-test", restored!.IndexedVersion);
        Assert.Equal("thumbnail.jpg", restored.ThumbnailPath);
        Assert.Single(await e.Db.MediaAsync(folder: folder.Id));
        await e.Db.ReconcileContentSourceAsync(folder.Id, new HashSet<string>(), default);
        Assert.Empty(await e.Db.MediaAsync(folder: folder.Id));
        Assert.Empty(await e.Db.SearchAsync(new float[] { 1, 0, 0 }, 5, default));
        await Assert.ThrowsAsync<ArgumentException>(() => e.Db.AddContentSourceAsync("../escape", "Bad", true));
    }
    [Fact]
    public async Task MobileInt8_RealFrenchSemanticRetrieval()
    {
        var models = Path.Combine(e.Root, "artifacts", "android", "assets", "models");
        Assert.True(File.Exists(Path.Combine(models, "manifest.json")), "Run scripts/prepare-android.ps1 first.");
        var paths = new FaceEngineTests.FacePaths(e.Paths, models);
        var manager = new ModelManager(paths);
        using var engine = new MobileEmbeddingService(manager, e.Images);
        await engine.InitializeAsync();
        var corpus = new[] { ("chelsea.png", "un chat"), ("coffee.png", "une tasse de café"), ("astronaut.png", "un astronaute") };
        var images = new List<float[]>();
        foreach (var (file, _) in corpus)
        {
            var vector = await engine.GenerateImageEmbeddingAsync(Path.Combine(e.Root, "tests", "fixtures", file), default);
            Assert.Equal(512, vector.Length); Assert.All(vector, v => Assert.True(float.IsFinite(v)));
            Assert.InRange(vector.Sum(v => (double)v * v), .999, 1.001); images.Add(vector);
        }
        foreach (var (_, query) in corpus)
        {
            var vector = await engine.GenerateTextEmbeddingAsync(query, default);
            var best = images.Select((image, index) => (index, similarity: image.Zip(vector).Sum(p => (double)p.First * p.Second))).MaxBy(p => p.similarity);
            Assert.Equal(Array.FindIndex(corpus, c => c.Item2 == query), best.index);
        }
    }
    [Fact]
    public async Task OverlappingGalleryAndAlbum_KeepMediaAndVectorsWhenOneSourceIsRemoved()
    {
        var gallery = await e.Db.AddContentSourceAsync("pixseek://gallery", "Gallery", true);
        var album = await e.Db.AddContentSourceAsync("pixseek://album/42", "Camera", true);
        var media = await e.Db.SaveMediaAsync(new() { LibraryFolderId = gallery.Id, FullPath = "content://media/external/images/media/84", DisplayName = "camera.jpg" });
        await e.Db.LinkContentMediaAsync(media.Id, gallery.Id, default);
        await e.Db.LinkContentMediaAsync(media.Id, album.Id, default);
        await e.Db.ConfigureVectorsAsync("overlap", 3);
        await e.Db.SaveVectorsAsync(media, "overlap", [new(0, "", [1, 0, 0])], default);
        Assert.Single(await e.Db.MediaAsync(folder: album.Id));
        Assert.Single(await e.Db.SearchFilteredAsync(new float[] { 1, 0, 0 }, 5, "Tout", album.Id));
        await e.Db.ReconcileContentSourceAsync(gallery.Id, new HashSet<string>(), default);
        Assert.Single(await e.Db.MediaAsync(folder: album.Id));
        await e.Db.RemoveContentSourceAsync(gallery.Id, default);
        Assert.Single(await e.Db.MediaAsync(folder: album.Id));
        Assert.Equal(album.Id, (await e.Db.FindAsync(media.FullPath))!.LibraryFolderId);
        Assert.Single(await e.Db.SearchAsync(new float[] { 1, 0, 0 }, 5, default));
    }
    [Theory]
    [InlineData("en", "Phone gallery")]
    [InlineData("fr", "Galerie du téléphone")]
    [InlineData("it", "Galleria del telefono")]
    [InlineData("de", "Galerie des Smartphones")]
    [InlineData("es", "Galería del teléfono")]
    public void MobileTranslations_AreEmbeddedAndComplete(string language, string expected)
    {
        var text = new MobileStrings(language);
        Assert.Equal(expected, text["DeviceGallery"]); Assert.NotEqual("AddPerson", text["AddPerson"]);
        Assert.NotEqual("SearchPlaceholder", text["SearchPlaceholder"]);
    }
}
