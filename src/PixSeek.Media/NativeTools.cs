using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Resolves only the application's staged tools, never arbitrary executables from PATH.</summary>
public sealed class NativeTools(IPlatformPaths paths, ProcessRunner runner)
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ToolCapabilities>>> Shared = new();
    public string CacheDirectory => paths.Cache;
    public string DataDirectory => paths.Data;
    public string NativeDirectory => paths.Native;
    public ToolCapabilities? Capabilities { get; private set; }

    public string PathFor(string name)
    {
        var executable = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        string[] aliases = name == "butteraugli" ? [executable, "butteraugli_main" + (OperatingSystem.IsWindows() ? ".exe" : "")] : [executable];
        foreach (var directory in new[] { paths.Native, Path.Combine(paths.Native, "compression", "bin"), Path.Combine(paths.Native, "compression"), Path.Combine(paths.Native, "media", "bin") })
            foreach (var alias in aliases)
                if (File.Exists(Path.Combine(directory, alias))) return Path.Combine(directory, alias);
        if (name == "exiftool" && OperatingSystem.IsWindows() && File.Exists(Path.Combine(paths.Native, "compression", "exiftool", "ExifTool.exe")))
            return Path.Combine(paths.Native, "compression", "exiftool", "ExifTool.exe");
        return Path.Combine(paths.Native, executable);
    }

    public string? ModelPath(string filename)
    {
        foreach (var directory in new[] { Path.Combine(paths.Native, "models", "vmaf_v1.0.16"), Path.Combine(paths.Native, "compression", "models", "vmaf_v1.0.16"), paths.Models })
            if (File.Exists(Path.Combine(directory, filename))) return Path.Combine(directory, filename);
        return null;
    }

    public async Task<ToolCapabilities> DetectAsync(CancellationToken ct = default)
    {
        // A fixture/application can construct many NativeTools instances. Share expensive probes;
        // cancellation of one caller must not poison detection for every other caller.
        var signature = new StringBuilder(Path.GetFullPath(paths.Native));
        foreach (var name in new[] { "ffmpeg", "ffprobe", "magick", "cjxl", "djxl", "avifenc", "avifdec", "heif-enc", "heif-dec", "ssimulacra2", "butteraugli", "vmaf", "exiftool" })
        {
            var file = new FileInfo(PathFor(name));
            signature.Append('|').Append(file.FullName).Append(':').Append(file.Exists ? file.Length : -1).Append(':').Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
        }
        foreach (var name in new[] { "vmaf_v1.0.16_3d0h.json", "vmaf_v1.0.16_1d5h_2160.json" })
        {
            var model = ModelPath(name);
            if (model != null) signature.Append('|').Append(model).Append(':').Append(File.GetLastWriteTimeUtc(model).Ticks);
        }
        // Refresh driver/device availability periodically, even without a restart.
        signature.Append('|').Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
        var lazy = Shared.GetOrAdd(key, _ => new(() => new EncoderCapabilityService(this, runner).DetectAsync(CancellationToken.None)));
        try { return Capabilities = await lazy.Value.WaitAsync(ct); }
        catch { if (lazy.IsValueCreated && lazy.Value.IsFaulted) Shared.TryRemove(key, out _); throw; }
    }
}
