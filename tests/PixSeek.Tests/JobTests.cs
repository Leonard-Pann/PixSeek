using PixSeek.Core;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;
public sealed class JobTests(TestEnvironment e) : IClassFixture<TestEnvironment>
{
    [Fact]
    public async Task CompressionJob_ResumesAfterCancellation_WithoutOverwritingResults()
    {
        var root = Path.Combine(e.Work, "job");
        Directory.CreateDirectory(root);
        foreach (var ext in new[]
        {
            "png",
            "jpg"
        }

        )
            await e.Images.RunAsync(["-size", "240x180", "gradient:navy-orange", Path.Combine(root, "même nom." + ext)]);
        var service = new MediaCompressionService(e.Db, e.Images, e.Probe, e.Encoder, new(e.Tools));
        var options = new CompressionOptions
        {
            KeepOnlySmaller = false
        };
        using (var control = new JobControl())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(root, options, control, new ImmediateProgress<WorkProgress>(p =>
            {
                if (p.Completed == 1)
                    control.Cancel();
            })));
        await using var c = await e.Db.OpenAsync();
        using var query = c.CreateCommand();
        query.CommandText = "SELECT Id FROM CompressionJob WHERE Status='Interrompu'";
        var id = (long)(await query.ExecuteScalarAsync())!;
        var outputFiles = Directory.GetFiles(Path.Combine(root, MediaFormats.OutputFolder), "*.jxl");
        Assert.Single(outputFiles);
        var first = await File.ReadAllBytesAsync(outputFiles[0]);
        CompressionReport resumed;
        using (var control = new JobControl())
            resumed = await service.RunAsync(root, options, control, new ImmediateProgress<WorkProgress>(_ =>
            {
            }));
        Assert.Equal(id, resumed.JobId);
        Assert.Equal(2, resumed.Items.Count);
        Assert.All(resumed.Items, r => Assert.Equal("Converti", r.Status));
        Assert.Equal(2, Directory.GetFiles(resumed.Destination, "*.jxl").Length);
        Assert.Equal(first, await File.ReadAllBytesAsync(outputFiles[0]));
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    [Fact]
    public async Task CustomOutput_UsesOnlySelectedFiles_ExcludesExports_AndPreservesSources()
    {
        var root = Path.Combine(e.Work, "sortie personnalisée");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "photo été.png");
        var other = Path.Combine(root, "autre.png");
        await e.Images.RunAsync(["-size", "120x80", "gradient:navy-orange", source]);
        File.Copy(source, other);
        var original = await File.ReadAllBytesAsync(source);
        var destination = Path.Combine(root, "Mes exports");
        var service = new MediaCompressionService(e.Db, e.Images, e.Probe, e.Encoder, new(e.Tools));
        using var control = new JobControl();
        var report = await service.RunAsync(root, new CompressionOptions { KeepOnlySmaller = false }, control, new ImmediateProgress<WorkProgress>(_ =>
        {
        }), [source], destination);
        var result = Assert.Single(report.Items);
        Assert.Equal("Converti", result.Status);
        Assert.Equal(source, result.SourcePath);
        Assert.StartsWith(destination + Path.DirectorySeparatorChar, result.DestinationPath);
        Assert.True(File.Exists(result.DestinationPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Equal(original, await File.ReadAllBytesAsync(other));
        // Previous custom exports are excluded even when the folder is analyzed again.
        var analysis = await service.AnalyzeAsync(root, true, CancellationToken.None);
        Assert.Equal(2, analysis.Photos);
        Assert.Single(analysis.Formats);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(root, new(), control, new ImmediateProgress<WorkProgress>(_ =>
        {
        }), destinationDirectory: root));
    }

    [Fact]
    public async Task PauseAndCancellation_AreCooperative()
    {
        using var control = new JobControl();
        control.Pause();
        var wait = control.CheckpointAsync();
        Assert.False(wait.IsCompleted);
        control.Resume();
        await wait;
        control.Pause();
        wait = control.CheckpointAsync();
        control.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
