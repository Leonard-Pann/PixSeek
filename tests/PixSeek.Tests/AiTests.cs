using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PixSeek.Tests;
public sealed class AiTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public void Preprocessing_UsesRgbNchwAndExactNormalization()
    {
        var rgb = new byte[224 * 224 * 3];
        rgb[0] = 255;
        var data = OnnxEmbeddingService.PreprocessRgb(rgb);
        Assert.Equal((1 - .48145466f) / .26862954f, data[0], 5);
        Assert.Equal(-.4578275f / .26130258f, data[224 * 224], 5);
        Assert.Equal(-.40821073f / .27577711f, data[2 * 224 * 224], 5);
        Assert.Throws<ArgumentException>(() => OnnxEmbeddingService.PreprocessRgb([1, 2, 3]));
    }

    [Fact]
    public async Task RealOnnx_FrenchRetrieval_AndVideoSceneSearch()
    {
        var modelPath = Path.Combine(e.Root, "models");
        Assert.True(File.Exists(Path.Combine(modelPath, "xlmr", "textual", "model.onnx")), "Lancez scripts/install-model.ps1 avant ce test d'intégration.");
        var paths = new ModelPaths(e.Paths, modelPath);
        var manager = new ModelManager(paths);
        using var embeddings = new OnnxEmbeddingService(manager, e.Images, new CpuExecutionProviderFactory());
        await embeddings.InitializeAsync();
        await e.Db.ConfigureVectorsAsync(manager.Version, manager.Manifest.Dimensions);
        var folder = await e.Db.AddFolderAsync(Path.Combine(e.Root, "tests", "fixtures"), true);
        var corpus = new[]
        {
            ("chelsea.png", "un chat"),
            ("coffee.png", "une tasse de café"),
            ("astronaut.png", "un astronaute")
        };
        foreach (var(file, _)in corpus)
        {
            var path = Path.Combine(folder.Path, file);
            var vector = await embeddings.GenerateImageEmbeddingAsync(path, default);
            Assert.Equal(512, vector.Length);
            Assert.All(vector, v => Assert.True(float.IsFinite(v)));
            Assert.InRange(vector.Sum(v => (double)v * v), .999, 1.001);
            var m = await e.Db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = path, MediaType = MediaType.Image });
            await e.Db.SaveVectorsAsync(m, manager.Version, [new(0, path, vector)], default);
            var repeated = await embeddings.GenerateImageEmbeddingAsync(path, default);
            Assert.InRange(vector.Zip(repeated, (a, b) => (double)a * b).Sum(), .9999, 1.0001);
        }

        foreach (var(file, query)in corpus)
        {
            var text = await embeddings.GenerateTextEmbeddingAsync(query, default);
            var results = await e.Db.SearchAsync(text, 3, default);
            Assert.Equal(file, results[0].Media.FileName);
        }

        var video = await e.Db.SaveMediaAsync(new() { LibraryFolderId = folder.Id, FullPath = Path.Combine(e.Work, "scènes.mp4"), MediaType = MediaType.Video });
        var cat = Path.Combine(folder.Path, "chelsea.png");
        await e.Db.SaveVectorsAsync(video, manager.Version, [new(152000, cat, await embeddings.GenerateImageEmbeddingAsync(cat, default))], default);
        var scenes = await e.Db.SearchAsync(await embeddings.GenerateTextEmbeddingAsync("un chat", default), 4, default);
        Assert.Contains(scenes, s => s.Media.Id == video.Id && s.TimestampMilliseconds == 152000);
        await File.WriteAllTextAsync(Path.Combine(e.Work, "onnx-inputs.txt"), embeddings.InputDescription);
    }

    private sealed class ModelPaths(IPlatformPaths source, string modelDirectory) : IPlatformPaths
    {
        public string Data => source.Data;
        public string Cache => source.Cache;
        public string Logs => source.Logs;
        public string Native => source.Native;
        public string Models => modelDirectory;
    }
}
