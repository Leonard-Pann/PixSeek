using PixSeek.Core;
using PixSeek.Infrastructure;
using Xunit;

namespace PixSeek.Tests;
public sealed class DatabaseTests : IClassFixture<TestEnvironment>
{
    private readonly TestEnvironment _e;
    public DatabaseTests(TestEnvironment e) => _e = e;
    [Fact]
    public async Task Schema_IsIdempotent_AndMediaCanBeUpdatedAndRemoved()
    {
        await _e.Db.InitializeAsync();
        await _e.Db.ConfigureVectorsAsync("test-v1", 3);
        var folder = await _e.Db.AddFolderAsync(_e.Work, true);
        var file = await _e.ImageAsync();
        var m = await _e.Db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = file, SizeBytes = 1 });
        m = await _e.Db.SaveMediaAsync(m with { SizeBytes = 42 });
        Assert.Equal(42, (await _e.Db.FindAsync(file))!.SizeBytes);
        await _e.Db.SaveVectorsAsync(m, "test-v1", [new(0, "", [1, 0, 0])], default);
        var found = await _e.Db.SearchAsync(new float[] { 1, 0, 0 }, 1, default);
        Assert.Single(found);
        Assert.Equal(m.Id, found[0].Media.Id);
        Assert.InRange(found[0].Similarity, .999, 1.001);
        await Assert.ThrowsAsync<ArgumentException>(() => _e.Db.SaveVectorsAsync(m, "test-v1", [new(0, "", [1, 0])], default));
        await _e.Db.RemoveFolderAsync(folder.Id);
        Assert.Null(await _e.Db.FindAsync(file));
        Assert.Empty(await _e.Db.SearchAsync(new float[] { 1, 0, 0 }, 1, default));
    }

    [Fact]
    public async Task ChangingModel_RebuildsVectorIndex()
    {
        await _e.Db.ConfigureVectorsAsync("model-a", 3);
        await _e.Db.ConfigureVectorsAsync("model-b", 4);
        Assert.Empty(await _e.Db.SearchAsync(new float[] { 1, 0, 0, 0 }, 5, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _e.Db.SearchAsync(new float[] { 1, 0 }, 5, default));
    }
}
