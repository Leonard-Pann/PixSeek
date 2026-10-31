using System.Text;

namespace PixSeek.Media;

/// <summary>UTF-8 argument files avoid the Windows command-line code page losing Unicode filenames or metadata.</summary>
internal static class ExifToolCommand
{
    public static async Task<ProcessResult> RunAsync(NativeTools tools, ProcessRunner runner, IEnumerable<string> arguments, CancellationToken ct)
    {
        Directory.CreateDirectory(tools.CacheDirectory);
        var name = ".exiftool-" + Guid.NewGuid().ToString("N") + ".args";
        var path = Path.Combine(tools.CacheDirectory, name);
        var args = arguments.ToArray();
        if (args.Any(a => a.Contains('\n') || a.Contains('\r')))
            throw new InvalidDataException("Argument de métadonnées multiligne non représentable sans ambiguïté ; conservation de l'original requise.");
        try
        {
            await File.WriteAllLinesAsync(path, args, new UTF8Encoding(false), ct);
            return await runner.RunAsync(tools.PathFor("exiftool"), ["-charset", "filename=UTF8", "-@", name], ct,
                timeout: TimeSpan.FromMinutes(2), workingDirectory: tools.CacheDirectory);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    public static async Task<string> CheckedAsync(NativeTools tools, ProcessRunner runner, IEnumerable<string> arguments, CancellationToken ct)
    {
        var result = await RunAsync(tools, runner, arguments, ct);
        if (result.ExitCode != 0) throw new InvalidDataException("ExifTool : " + result.StandardError.Trim());
        return result.StandardOutput;
    }
}
