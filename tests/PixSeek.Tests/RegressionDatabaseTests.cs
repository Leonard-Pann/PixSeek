using Microsoft.Data.Sqlite;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;

public sealed class RegressionDatabaseTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task FilteredKnn_FiltersBeforeRankingAcross3200Vectors_AndRepairsLegacyColumns()
    {
        await e.Db.ConfigureVectorsAsync("filter-test", 3);
        Directory.CreateDirectory(Path.Combine(e.Work, "a"));
        Directory.CreateDirectory(Path.Combine(e.Work, "b"));
        var a = await e.Db.AddFolderAsync(Path.Combine(e.Work, "a"), true);
        var b = await e.Db.AddFolderAsync(Path.Combine(e.Work, "b"), true);
        await using (var connection = await e.Db.OpenAsync())
        {
            using var transaction = connection.BeginTransaction();
            for (var i = 0; i < 3200; i++)
            {
                var isVideo = i < 2400;
                var folder = i >= 3150 ? b : a;
                var media = new MediaFile { Id = i + 1, LibraryFolderId = folder.Id,
                    FullPath = Path.Combine(folder.Path, $"item-{i}" + (isVideo ? ".mp4" : ".jpg")),
                    MediaType = isVideo ? MediaType.Video : MediaType.Image, IndexedVersion = "filter-test" };
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO MediaFile(Id,LibraryFolderId,FullPath,FileName,Extension,MediaType,Status,IndexedVersion,Payload)
                    VALUES($id,$folder,$path,$name,$ext,$type,'Indexé','filter-test',$json);
                    INSERT INTO MediaEmbedding(Id,MediaFileId,ModelId,Vector) VALUES($id,$id,'filter-test',$vector);
                    INSERT INTO MediaVector(rowid,embedding) VALUES($id,$vector);
                    """;
                command.Parameters.AddWithValue("$id", media.Id);
                command.Parameters.AddWithValue("$folder", folder.Id);
                command.Parameters.AddWithValue("$path", media.FullPath);
                command.Parameters.AddWithValue("$name", media.FileName);
                command.Parameters.AddWithValue("$ext", media.Extension);
                command.Parameters.AddWithValue("$type", (int)media.MediaType);
                command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(media));
                command.Parameters.AddWithValue("$vector", LibraryDatabase.VectorBytes(isVideo ? [1f, 0f, 0f] : i < 3150 ? [.8f, .6f, 0f] : [.6f, .8f, 0f]));
                await command.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        var photos = await e.Db.SearchFilteredAsync(new float[] { 1, 0, 0 }, 240, "Photos", null);
        Assert.Equal(240, photos.Count);
        Assert.All(photos, p => Assert.Equal(MediaType.Image, p.Media.MediaType));
        var rare = await e.Db.SearchFilteredAsync(new float[] { 1, 0, 0 }, 240, "Photos", b.Id);
        Assert.Equal(50, rare.Count);
        Assert.All(rare, p => Assert.Equal(b.Id, p.Media.LibraryFolderId));
        Assert.Equal(800, (await e.Db.MediaAsync(0, 4000, "Photos")).Count);
        var existing = photos[0].Media;
        await using (var connection = await e.Db.OpenAsync())
            await LibraryDatabase.ExecuteAsync(connection, "UPDATE MediaFile SET MediaType=1 WHERE MediaType=0; DELETE FROM SchemaMigration WHERE Version=2;");
        await e.Db.InitializeAsync();
        Assert.Equal(800, (await e.Db.MediaAsync(0, 4000, "Photos")).Count);
        // Upserts must repair the relational filter columns as well as the JSON payload.
        await e.Db.SaveMediaAsync(existing with { MediaType = MediaType.Video, LibraryFolderId = b.Id });
        Assert.Contains(await e.Db.MediaAsync(0, 4000, "Vidéos", folder: b.Id), p => p.Id == existing.Id);
        await e.Db.SaveMediaAsync(existing);
        Assert.Contains(await e.Db.MediaAsync(0, 4000, "Photos", folder: a.Id), p => p.Id == existing.Id);
    }

    [Fact]
    public void CodecDefaults_AreCentralizedAndCapabilityAware()
    {
        var policy = new CompressionCodecPolicy();
        var capabilities = new ToolCapabilities("", "", "libsvtav1", true, true, "", "");
        Assert.Equal("JPEG XL", policy.ImageDefault(capabilities));
        Assert.Equal("AV1", policy.VideoDefault(capabilities));
        Assert.Equal("AVIF", policy.ImageDefault(capabilities with { Jxl = false }));
        Assert.Equal("Conserver", policy.ImageDefault(null));
        Assert.Equal("Conserver", policy.VideoDefault(capabilities with { Av1Encoder = null }));
    }
}
