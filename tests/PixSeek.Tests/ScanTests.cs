using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;
public sealed class ScanTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task Discovery_Recurses_ExcludesOutputsAndSymbolicLinks()
    {
        var root = Path.Combine(e.Work, "scan");
        Directory.CreateDirectory(root);
        Assert.Empty(FileDiscovery.Enumerate(root, true));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        Directory.CreateDirectory(Path.Combine(root, MediaFormats.OutputFolder));
        await File.WriteAllTextAsync(Path.Combine(root, "a.jpg"), "bad");
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "b.mp4"), "bad");
        await File.WriteAllTextAsync(Path.Combine(root, MediaFormats.OutputFolder, "output.jxl"), "bad");
        Assert.Equal(2, FileDiscovery.Enumerate(root, true).Count());
        Assert.Single(FileDiscovery.Enumerate(root, false));
        Assert.Throws<DirectoryNotFoundException>(() => FileDiscovery.Enumerate(root + "missing", true).ToArray());
    }

    [Fact]
    public void BlankModelEnvironment_UsesDataDirectory()
    {
        var current = Environment.GetEnvironmentVariable("PIXSEEK_MODEL_DIR");
        var legacy = Environment.GetEnvironmentVariable("LUMINA_MODEL_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PIXSEEK_MODEL_DIR", " ");
            Environment.SetEnvironmentVariable("LUMINA_MODEL_DIR", "");
            var data = Path.Combine(e.Work, "empty environment");
            var paths = new PlatformPaths(data, e.Paths.Native);
            Assert.Equal(Path.Combine(data, "models"), paths.Models);
            Assert.True(Directory.Exists(paths.Models));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIXSEEK_MODEL_DIR", current);
            Environment.SetEnvironmentVariable("LUMINA_MODEL_DIR", legacy);
        }
    }

    [Fact]
    public void FrameSampling_IsBoundedAndSpansLongVideos()
    {
        var frames = FfmpegService.FrameTimestamps(3_600_000, 10, 12);
        Assert.Equal(12, frames.Count);
        Assert.Equal(0, frames[0]);
        Assert.InRange(frames[^1], 3_599_000, 3_599_999);
        Assert.Single(FfmpegService.FrameTimestamps(1000, 10, 120));
    }
}
