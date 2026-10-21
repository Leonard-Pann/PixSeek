using PixSeek.AI;
using Xunit;

namespace PixSeek.Tests;
public sealed class FaceEngineTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task YuNetAndSFace_RealDetectionAndNormalizedIdentityVectors()
    {
        var paths = new FacePaths(e.Paths, Path.Combine(e.Root, "artifacts", "models"));
        var manager = new FaceModelManager(paths);
        using var engine = new FaceEmbeddingService(manager, e.Images, paths);
        var astronaut = Path.Combine(e.Root, "tests", "fixtures", "astronaut.png");
        var faces = await engine.DetectAsync(astronaut, default);
        Assert.Single(faces);
        Assert.Equal(128, faces[0].Vector.Length);
        Assert.InRange(faces[0].Vector.Sum(v => (double)v * v), .999, 1.001);
        var transformed = Path.Combine(e.Work, "same-person.jpg");
        await e.Images.RunAsync([astronaut, "-resize", "75%", "-modulate", "95,100,100", transformed]);
        var second = await engine.DetectAsync(transformed, default);
        Assert.Single(second);
        var similarity = faces[0].Vector.Zip(second[0].Vector).Sum(pair => (double)pair.First * pair.Second);
        Assert.True(similarity > FaceEmbeddingService.MatchThreshold, $"Similarity {similarity}");
        var other = await engine.DetectAsync(Path.Combine(e.Root, "tests", "fixtures", "neil-armstrong.jpg"), default);
        Assert.Single(other);
        var different = faces[0].Vector.Zip(other[0].Vector).Sum(pair => (double)pair.First * pair.Second);
        Assert.True(different < FaceEmbeddingService.MatchThreshold, $"Different-person similarity {different}");
        Assert.Empty(await engine.DetectAsync(Path.Combine(e.Root, "tests", "fixtures", "coffee.png"), default));
    }

    internal sealed class FacePaths(PixSeek.Core.IPlatformPaths source, string models) : PixSeek.Core.IPlatformPaths
    {
        public string Data => source.Data; public string Cache => source.Cache; public string Models => models;
        public string Logs => source.Logs; public string Native => source.Native;
    }
}
