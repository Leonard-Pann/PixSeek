using PixSeek.Core;
using PixSeek.Infrastructure;
using Xunit;

namespace PixSeek.Tests;

public sealed class PersonFilterDatabaseTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task PersonFilter_CoversAllPagesAndSelection_AndAppliesBeforeSemanticTopK()
    {
        var people = new PeopleRepository(e.Db);
        await people.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(e.Work, "person-filter"));
        Directory.CreateDirectory(Path.Combine(e.Work, "other-person-filter"));
        var folder = await e.Db.AddFolderAsync(Path.Combine(e.Work, "person-filter"), true);
        var otherFolder = await e.Db.AddFolderAsync(Path.Combine(e.Work, "other-person-filter"), true);
        const string version = "sql-face-filter-test";
        // Deliberate unit vectors test SQL mechanics, not face recognition (covered by real-model tests).
        var a = new float[128]; a[0] = 1;
        var b = new float[128]; b[1] = 1;
        var reference = new DetectedFace("reference.png", "", a, 1, 0, 0, 100, 100);
        var person = await people.AddAsync(["Alex"], [reference], version, default);
        await e.Db.ConfigureVectorsAsync("filter-test", 2);
        for (var i = 0; i < 270; i++)
        {
            var media = await e.Db.SaveMediaAsync(new() { LibraryFolderId = folder.Id,
                FullPath = Path.Combine(folder.Path, $"photo-{i:D4}.jpg"), LastModifiedAt = DateTime.UtcNow,
                SizeBytes = 123, Status = "Indexé", IndexedVersion = "filter-test" });
            await people.SaveFacesAsync(media, [reference with { Vector = i < 260 ? a : b }], version, default);
            await e.Db.SaveVectorsAsync(media, "filter-test", [new(0, "", i < 260 ? new float[] { 0, 1 } : new float[] { 1, 0 })], default);
        }
        var first = await e.Db.MediaAsync(type: "Photos", personId: person, faceVersion: version);
        Assert.Equal(240, first.Count);
        Assert.Equal(20, (await e.Db.MediaAsync(offset: 240, type: "Photos", personId: person, faceVersion: version)).Count);
        var selection = await e.Db.SelectionAsync("Photos", folder.Id, person, version, .45);
        Assert.Equal(260, selection.Count);
        Assert.Empty(await e.Db.SelectionAsync("Photos", otherFolder.Id, person, version, .45));
        Assert.Empty(await e.Db.SelectionAsync("Vidéos", null, person, version, .45));
        Assert.Empty(await e.Db.SelectionAsync("Photos", null, person, "different-model", .45));
        var nearest = await e.Db.SearchFilteredAsync(new float[] { 1, 0 }, 5, "Photos", folder.Id, personId: person, faceVersion: version);
        Assert.Equal(5, nearest.Count);
        Assert.All(nearest, r => Assert.Contains(selection, s => s.Id == r.Media.Id));
        var names = await e.Db.FilenameCandidatesAsync("photo-0269", personId: person, faceVersion: version);
        Assert.DoesNotContain(names, m => m.FileName == "photo-0269.jpg");
        var changed = await e.Db.FindAsync(selection[0].Path);
        await e.Db.SaveMediaAsync(changed! with { SizeBytes = 456 });
        Assert.Equal(259, (await e.Db.SelectionAsync("Photos", folder.Id, person, version, .45)).Count);
        await people.DeleteAsync(person);
        Assert.Empty(await e.Db.MediaAsync(personId: person, faceVersion: version));
    }
}
