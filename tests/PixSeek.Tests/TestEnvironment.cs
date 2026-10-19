using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PixSeek.Tests;
public sealed class TestEnvironment : IAsyncLifetime
{
    public string Root { get; } = FindRoot();
    public PlatformPaths Paths { get; private set; } = null!;
    public LibraryDatabase Db { get; private set; } = null!;
    public ProcessRunner Runner { get; } = new(NullLogger<ProcessRunner>.Instance);
    public NativeTools Tools { get; private set; } = null!;
    public ImageMagickService Images { get; private set; } = null!;
    public FfprobeService Probe { get; private set; } = null!;
    public FfmpegService Video { get; private set; } = null!;
    public MediaEncoder Encoder { get; private set; } = null!;
    public string Work => Paths.Data;

    public static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PixSeek.slnx")))
            d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable");
    }

    public async ValueTask InitializeAsync()
    {
        Paths = new(Path.Combine(Root, "artifacts", "tests", Guid.NewGuid().ToString("N")), Path.Combine(Root, "runtimes", "win-x64", "native"));
        Db = new(Paths);
        await Db.InitializeAsync();
        Tools = new(Paths, Runner);
        await Tools.DetectAsync();
        Images = new(Tools, Runner);
        Probe = new(Tools, Runner);
        Video = new(Tools, Runner);
        Encoder = new(Tools, Runner, new(Tools), new(Images, Probe, Video, Tools, Runner));
    }

    public async Task<string> ImageAsync(string name = "photo avec accents é.png", string color = "red")
    {
        var p = Path.Combine(Work, name);
        await Images.RunAsync(["-size", "320x240", "xc:" + color, "-fill", "white", "-draw", "circle 160,120 160,30", p]);
        return p;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
