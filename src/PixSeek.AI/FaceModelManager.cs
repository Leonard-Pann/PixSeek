using System.Security.Cryptography;
using System.Text.Json;
using PixSeek.Core;

namespace PixSeek.AI;

public sealed class FaceModelManager(IPlatformPaths paths)
{
    public ModelManifest Manifest { get; } = JsonSerializer.Deserialize<ModelManifest>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models", "faces.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    public string Version => Manifest.Revision + ":" + Manifest.PreprocessingVersion;
    public string DirectoryPath => Path.Combine(paths.Models, "faces");
    private readonly SemaphoreSlim _gate = new(1);
    private bool _validated;

    public async Task EnsureAsync(IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_validated) return;
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            long complete = 0, total = Manifest.Files.Sum(a => a.Size);
            foreach (var asset in Manifest.Files)
            {
                var target = Path.Combine(DirectoryPath, asset.Path);
                if (!File.Exists(target) || new FileInfo(target).Length != asset.Size)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    var temporary = target + ".partial";
                    try
                    {
                        using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        await using (var input = await response.Content.ReadAsStreamAsync(ct))
                        await using (var output = File.Create(temporary))
                        {
                            var buffer = new byte[81920];
                            long downloaded = 0;
                            int read;
                            while ((read = await input.ReadAsync(buffer, ct)) > 0)
                            {
                                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                                downloaded += read;
                                progress?.Report(new("Face models", Fraction: (complete + downloaded) / (double)total));
                            }
                        }
                        await ValidateAsync(temporary, asset, ct);
                        File.Move(temporary, target, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                await ValidateAsync(target, asset, ct);
                complete += asset.Size;
            }
            _validated = true;
        }
        finally { _gate.Release(); }
    }

    private static async Task ValidateAsync(string path, ModelAsset asset, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        if (file.Length != asset.Size || !Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid face model checksum: " + asset.Path);
    }
}
