using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Xunit;

namespace PixSeek.Tests;

public sealed class CompleteExportTests
{
    private static readonly IProgress<EncodingProgress> NoProgress = new ImmediateProgress<EncodingProgress>(_ => { });
    private static (PlatformPaths Paths, LibraryDatabase Db, MediaEncoder Encoder, MediaCompressionService Service) Environment()
    {
        var root = Path.Combine(TestEnvironment.FindRoot(), "artifacts", "tests", "complete-export-" + Guid.NewGuid().ToString("N"));
        var paths = new PlatformPaths(root, Path.Combine(root, "no-native-tools"));
        var db = new LibraryDatabase(paths);
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var tools = new NativeTools(paths, runner);
        var images = new ImageMagickService(tools, runner); var probe = new FfprobeService(tools, runner);
        var resolver = new CompressionProfileResolver(tools);
        var encoder = new MediaEncoder(tools, runner, resolver, new(images, probe, new(tools, runner), tools, runner));
        return (paths, db, encoder, new(db, images, probe, encoder, resolver));
    }
    private static async Task<string> Write(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content); return path;
    }
    [Theory]
    [InlineData(MediaType.Image, ".jxl")]
    [InlineData(MediaType.Video, ".mkv")]
    public async Task KeepOriginal_ExportsBytesWithoutAnyNativeTool(MediaType type, string extension)
    {
        var e = Environment();
        var source = await Write(e.Paths.Data, "input/original" + extension, "All bytes, private metadata and tracks remain intact.\0\u00e9");
        var target = Path.Combine(e.Paths.Data, "output/original" + extension);
        var result = await e.Encoder.EncodeAsync(new(source, target, type, new(0, 0, 0, ""), new() { ImageCodec = "original", VideoCodec = "original", KeepMetadata = false }), NoProgress, default);
        Assert.Equal("Copié", result.Status); Assert.Equal(Path.GetFullPath(target), result.DestinationPath);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(target));
        Assert.True(await OriginalMediaExporter.IsVerifiedAsync(result)); Assert.Equal(0, result.SavedBytes);
    }
    [Fact]
    public async Task EncodingFailure_StillExportsUnchangedSource_AndReportsZeroSavings()
    {
        var e = Environment(); var source = await Write(e.Paths.Data, "source/photo.jpg", "Native tools deliberately unavailable");
        var target = Path.Combine(e.Paths.Data, "output/photo.jpg");
        var result = await e.Encoder.EncodeAsync(new(source, Path.Combine(e.Paths.Data, "output/photo"), MediaType.Image, new(0, 0, 0, ""), new())
        { DestinationIsStem = true, OriginalDestinationPath = target }, NoProgress, default);
        Assert.Equal("Copié", result.Status); Assert.Null(result.Error); Assert.NotNull(result.OutputSha256);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(target));
        Assert.True(new CompressionReport(0, Path.GetDirectoryName(target)!, [result]).IsComplete);
    }
    [Fact]
    public async Task CopyCollision_NeverOverwrites_AndRepeatUsesVerifiedExistingCopy()
    {
        var e = Environment(); var source = await Write(e.Paths.Data, "source/photo.avif", "original-avif");
        var target = await Write(e.Paths.Data, "output/photo.avif", "unrelated-existing-file");
        var request = new EncodingRequest(source, target, MediaType.Image, new(0, 0, 0, ""), new());
        var exporter = new OriginalMediaExporter();
        var a = await exporter.CopyAsync(request, "Not re-encoded", NoProgress, default);
        var stamp = File.GetLastWriteTimeUtc(a.DestinationPath!);
        var b = await exporter.CopyAsync(request, "Repeat", NoProgress, default);
        Assert.NotEqual(target, a.DestinationPath); Assert.Equal(a.DestinationPath, b.DestinationPath);
        Assert.Equal("unrelated-existing-file", await File.ReadAllTextAsync(target));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(b.DestinationPath!));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(target)!).Length);
    }
    [Fact]
    public async Task CopyCancellation_NeverPublishesPartialFile()
    {
        var e = Environment(); var source = await Write(e.Paths.Data, "source/image.png", new string('x', 100_000));
        var target = Path.Combine(e.Paths.Data, "output/image.png");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OriginalMediaExporter().CopyAsync(new(source, target, MediaType.Image, new(0, 0, 0, ""), new()), "Cancelled", NoProgress, cancel.Token));
        Assert.False(File.Exists(target)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!)); Assert.True(File.Exists(source));
    }
    [Fact]
    public async Task FolderExport_IncludesUnencodableMediaAndSidecars_PreservesTreeAndNames()
    {
        var e = Environment(); await e.Db.InitializeAsync();
        var input = Path.Combine(e.Paths.Data, "source"); var output = Path.Combine(e.Paths.Data, "output");
        string[] relative = ["same.jpg", "same.png", "nested/same.jpg", "nested/movie.mkv", "nested/modern.jxl", "nested/raw.nef", "nested/raw.xmp", "nested/movie.fr.srt", "nested/legacy.mxf", "nested/raw.nef.json", "PixSeek Optimized/legitimate.png"];
        foreach (var path in relative) await Write(input, path, "Original " + path);
        using var control = new JobControl();
        var report = await e.Service.RunAsync(input, new() { ImageCodec = "original", VideoCodec = "original", KeepMetadata = false, Recursive = false }, control, new ImmediateProgress<WorkProgress>(_ => { }), destinationDirectory: output);
        Assert.True(report.IsComplete, report.CompletionIssue); Assert.Equal(relative.Length, report.CopiedCount); Assert.Equal(0, report.SavedBytes);
        foreach (var path in relative) Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(input, path)), await File.ReadAllBytesAsync(Path.Combine(output, path)));
        Assert.All(report.Items, item => Assert.True(item.IsExported));
    }
    [Fact]
    public async Task InterruptedCopiedJob_ResumesAndRepairsCorruptedExportWithoutOverwritingIt()
    {
        var e = Environment(); await e.Db.InitializeAsync();
        var input = Path.Combine(e.Paths.Data, "source"); var output = Path.Combine(e.Paths.Data, "output");
        await Write(input, "a.jpg", "source-A"); await Write(input, "b.mp4", "source-B");
        var options = new CompressionOptions { ImageCodec = "original", VideoCodec = "original" };
        using (var control = new JobControl())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Service.RunAsync(input, options, control,
                new ImmediateProgress<WorkProgress>(p => { if (p.Completed == 1) control.Cancel(); }), destinationDirectory: output));
        var badOutput = Path.Combine(output, "a.jpg"); Assert.True(File.Exists(badOutput));
        var stamp = File.GetLastWriteTimeUtc(badOutput);
        await File.WriteAllTextAsync(badOutput, "bad-data"); File.SetLastWriteTimeUtc(badOutput, stamp);
        using var resumed = new JobControl();
        var report = await e.Service.RunAsync(input, options, resumed, new ImmediateProgress<WorkProgress>(_ => { }), destinationDirectory: output);
        Assert.True(report.IsComplete, report.CompletionIssue); Assert.Equal(2, report.CopiedCount);
        var repaired = report.Items.Single(i => Path.GetFileName(i.SourcePath) == "a.jpg");
        Assert.NotEqual(badOutput, repaired.DestinationPath); Assert.True(await OriginalMediaExporter.IsVerifiedAsync(repaired));
        Assert.Equal("bad-data", await File.ReadAllTextAsync(badOutput));
    }
    [Fact]
    public async Task SourceChangesDuringExport_AreReportedAsIncomplete()
    {
        var e = Environment(); await e.Db.InitializeAsync(); var input = Path.Combine(e.Paths.Data, "source");
        await Write(input, "a.jpg", "A"); await Write(input, "b.jpg", "B"); var added = false;
        using var control = new JobControl();
        var report = await e.Service.RunAsync(input, new() { ImageCodec = "original", VideoCodec = "original" }, control,
            new ImmediateProgress<WorkProgress>(p => { if (p.Completed == 1 && !added) { added = true; File.WriteAllText(Path.Combine(input, "new.jpg"), "new"); } }), destinationDirectory: Path.Combine(e.Paths.Data, "output"));
        Assert.False(report.IsComplete); Assert.Contains("changé", report.CompletionIssue);
        await using var c = await e.Db.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Status FROM CompressionJob WHERE Id=" + report.JobId;
        Assert.Equal("Incomplet", await cmd.ExecuteScalarAsync());
    }
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    [Fact]
    public async Task CopyFailure_MarksWholeExportIncomplete_AndContinuesWithRemainingMedia()
    {
        var e = Environment(); await e.Db.InitializeAsync(); var input = Path.Combine(e.Paths.Data, "source"); var output = Path.Combine(e.Paths.Data, "output");
        var source = await Write(input, "a.jpg", "Do not lose these bytes"); await Write(input, "b.jpg", "B");
        Directory.CreateDirectory(Path.Combine(output, "a.jpg")); // Cannot publish a file at a directory's path.
        using var control = new JobControl();
        var report = await e.Service.RunAsync(input, new() { ImageCodec = "original" }, control, new ImmediateProgress<WorkProgress>(_ => { }), destinationDirectory: output);
        Assert.False(report.IsComplete); Assert.Single(report.Items, i => i.Status == "Échec"); Assert.Single(report.Items, i => i.IsExported);
        Assert.Equal("Do not lose these bytes", await File.ReadAllTextAsync(source));
        Assert.DoesNotContain(Directory.GetFiles(output), f => Path.GetFileName(f).StartsWith(".pixseek", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selection_AlsoExportsMatchingMetadataSidecars()
    {
        var e = Environment(); await e.Db.InitializeAsync(); var input = Path.Combine(e.Paths.Data, "source");
        var photo = await Write(input, "photo.jpg", "Photo"); await Write(input, "photo.xmp", "Metadata"); await Write(input, "unselected.xmp", "Other metadata");
        using var control = new JobControl();
        var report = await e.Service.RunAsync(input, new() { ImageCodec = "original" }, control, new ImmediateProgress<WorkProgress>(_ => { }), [photo], Path.Combine(e.Paths.Data, "output"));
        Assert.True(report.IsComplete); Assert.Equal(2, report.CopiedCount); Assert.Contains(report.Items, i => i.SourcePath.EndsWith("photo.xmp"));
        Assert.DoesNotContain(report.Items, i => i.SourcePath.EndsWith("unselected.xmp"));
    }
    [Fact]
    public async Task HistoricalOutputInsideSource_IsIncludedInsteadOfSilentlyExcluded()
    {
        var e = Environment(); await e.Db.InitializeAsync(); var input = Path.Combine(e.Paths.Data, "source");
        var oldOutput = Path.Combine(input, "old-export");
        await Write(input, "photo.jpg", "Photo"); await Write(oldOutput, "unique.jxl", "A unique historical export");
        await using (var c = await e.Db.OpenAsync())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO CompressionJob(SourceDirectory,DestinationDirectory,Preset,Status,StartedAt,OptionsJson) VALUES($s,$d,'Quality','Terminé',CURRENT_TIMESTAMP,'{}')";
            cmd.Parameters.AddWithValue("$s", input); cmd.Parameters.AddWithValue("$d", oldOutput);
            await cmd.ExecuteNonQueryAsync();
        }
        using var control = new JobControl(); var output = Path.Combine(e.Paths.Data, "output");
        var report = await e.Service.RunAsync(input, new() { ImageCodec = "original" }, control, new ImmediateProgress<WorkProgress>(_ => { }), destinationDirectory: output);
        Assert.True(report.IsComplete); Assert.Equal(2, report.CopiedCount);
        Assert.Equal("A unique historical export", await File.ReadAllTextAsync(Path.Combine(output, "old-export", "unique.jxl")));
    }
    [Fact]
    public void Destination_CannotBeInsideSource_AndDefaultIsASibling()
    {
        var root = Path.Combine(TestEnvironment.FindRoot(), "artifacts", "tests", "paths-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Assert.Throws<ArgumentException>(() => ExportPathPolicy.EnsureIndependent(source, source));
        Assert.Throws<ArgumentException>(() => ExportPathPolicy.EnsureIndependent(source, Path.Combine(source, "output")));
        var destination = ExportPathPolicy.DefaultDestination(source);
        Assert.Equal(root, Path.GetDirectoryName(destination));
        ExportPathPolicy.EnsureIndependent(source, destination);
    }
}
