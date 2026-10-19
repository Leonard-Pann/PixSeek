using System;
using Avalonia;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixSeek.AI;
using PixSeek.Core;
using PixSeek.Infrastructure;
using PixSeek.Media;
using Microsoft.Extensions.DependencyInjection;
using PixSeek.Services;

namespace PixSeek.Desktop;
sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--diagnostics")
            return DiagnosticsAsync(args.Length > 1 ? args[1] : null).GetAwaiter().GetResult();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Environment.GetEnvironmentVariable("PIXSEEK_DATA_DIR"))))[..24];
        using var instance = new Mutex(true, "PixSeek-" + identity, out var created);
        if (!created)
            return 2;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            var paths = new PlatformPaths();
            File.AppendAllText(Path.Combine(paths.Logs, "startup-error.log"), DateTimeOffset.Now + " " + e + Environment.NewLine);
            return 1;
        }
    }

    private static async Task<int> DiagnosticsAsync(string? output)
    {
        try
        {
            using var services = AppServices.Create();
            var db = services.GetRequiredService<LibraryDatabase>();
            await db.InitializeAsync();
            var tools = await services.GetRequiredService<NativeTools>().DetectAsync();
            var model = services.GetRequiredService<ModelManager>();
            string ai = "Poids absents";
            if (model.FilesPresent)
            {
                var embedding = services.GetRequiredService<OnnxEmbeddingService>();
                var text = await embedding.GenerateTextEmbeddingAsync("un chien", CancellationToken.None);
                ai = $"ONNX CPU prêt : {text.Length} dimensions, norme {Math.Sqrt(System.Linq.Enumerable.Sum(text, x => (double)x * x)):F6}";
                await db.ConfigureVectorsAsync(model.Version, model.Manifest.Dimensions);
            }

            var json = JsonSerializer.Serialize(new { DotNet = Environment.Version.ToString(), tools.FfmpegVersion, tools.MagickVersion, tools.Av1Encoder, tools.Jxl, tools.Avif, tools.NativeDirectory, SqliteVec = db.VectorStatus, AI = ai }, new JsonSerializerOptions { WriteIndented = true });
            if (output != null)
                await File.WriteAllTextAsync(output, json);
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception e)
        {
            if (output != null)
                await File.WriteAllTextAsync(output, e.ToString());
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
