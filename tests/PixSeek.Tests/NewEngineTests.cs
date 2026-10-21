using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using Xunit;

namespace PixSeek.Tests;
public sealed class NewEngineTests(TestEnvironment environment) : IClassFixture<TestEnvironment>
{
    [Fact]
    public void HybridSearch_BoostsOnlyStrongNames_AndPreservesSemanticOrder()
    {
        Assert.Equal(1, HybridRanking.NameConfidence("été vacances", "Été_vacances.jpg"));
        Assert.True(HybridRanking.NameConfidence("vacances", "mes-vacances-2026.jpg") > .9);
        Assert.True(HybridRanking.NameConfidence("IMG_1235", "IMG_1234.jpg") > .84);
        Assert.Equal(0, HybridRanking.NameConfidence("a dog", "IMG_1234.jpg"));
        var named = new MediaFile
        {
            Id = 1,
            FullPath = "Holiday beach.jpg"
        };
        var semantic = new MediaFile
        {
            Id = 2,
            FullPath = "DSC123.jpg"
        };
        var results = new SearchResult[]
        {
            new(semantic, .8, null, ""),
            new(named, .2, null, "")
        };
        Assert.Equal(1, HybridRanking.Merge("Holiday beach", results, [], 10)[0].Media.Id);
        Assert.Equal(2, HybridRanking.Merge("a sunset", results, [], 10)[0].Media.Id);
    }

    [Fact]
    public async Task Siglip2_RealOnnx_FrenchRetrieval_AndSwitchingDimensions()
    {
        var paths = new TestPaths(environment.Paths, Path.Combine(environment.Root, "models"));
        var models = new ModelManager(paths);
        models.Select("siglip2");
        using var engine = new OnnxEmbeddingService(models, environment.Images, new CpuExecutionProviderFactory());
        var fixture = Path.Combine(environment.Root, "tests", "fixtures");
        var cat = await engine.GenerateImageEmbeddingAsync(Path.Combine(fixture, "chelsea.png"), default);
        var coffee = await engine.GenerateImageEmbeddingAsync(Path.Combine(fixture, "coffee.png"), default);
        var astronaut = await engine.GenerateImageEmbeddingAsync(Path.Combine(fixture, "astronaut.png"), default);
        Assert.Equal(768, cat.Length);
        Assert.All(cat, n => Assert.True(float.IsFinite(n)));
        Assert.InRange(cat.Sum(n => (double)n * n), .999, 1.001);
        foreach (var(query, correct)in new[]
        {
            ("un chat", cat),
            ("une tasse de café", coffee),
            ("un astronaute", astronaut)
        }

        )
        {
            var text = await engine.GenerateTextEmbeddingAsync(query, default);
            double Dot(float[] vector) => text.Zip(vector).Sum(pair => (double)pair.First * pair.Second);
            Assert.Equal(correct, new[] { cat, coffee, astronaut }.OrderByDescending(Dot).First());
        }

        await environment.Db.ConfigureVectorsAsync(models.Version, 768);
        models.Select("xlmr");
        await engine.ResetAsync();
        var xlmr = await engine.GenerateTextEmbeddingAsync("un chat", default);
        Assert.Equal(512, xlmr.Length);
        await environment.Db.ConfigureVectorsAsync(models.Version, 512);
    }

    private sealed class TestPaths(IPlatformPaths source, string models) : IPlatformPaths
    {
        public string Data => source.Data;
        public string Cache => source.Cache;
        public string Models => models;
        public string Logs => source.Logs;
        public string Native => source.Native;
    }
}
