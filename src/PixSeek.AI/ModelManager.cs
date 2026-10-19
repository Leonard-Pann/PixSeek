using System.Security.Cryptography;
using System.Text.Json;
using PixSeek.Core;

namespace PixSeek.AI;
public sealed record ModelAsset(string Path, long Size, string Sha256, string Url);
public sealed record ModelManifest(string Id, string Revision, int Dimensions, string License, string PreprocessingVersion, IReadOnlyList<ModelAsset> Files);
public sealed class ModelManager(IPlatformPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public ModelManifest Manifest { get; } = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models", "manifest.json")), JsonOptions)!;
    public string Version => Manifest.Revision + ":" + Manifest.PreprocessingVersion;
    private string BundledPath => Path.Combine(AppContext.BaseDirectory, "models", "weights");
    public string DirectoryPath => PresentIn(paths.Models) ? paths.Models : PresentIn(BundledPath) ? BundledPath : paths.Models;

    private bool PresentIn(string directory) => Manifest.Files.All(f => File.Exists(Path.Combine(directory, f.Path)) && new FileInfo(Path.Combine(directory, f.Path)).Length == f.Size);
    public bool FilesPresent => PresentIn(DirectoryPath);
    public string Status { get; private set; } = "Modèle non chargé";

    public async Task ValidateAsync(CancellationToken ct = default)
    {
        foreach (var f in Manifest.Files)
        {
            var target = Path.Combine(DirectoryPath, f.Path);
            if (!File.Exists(target))
                throw new FileNotFoundException($"Modèle incomplet : {f.Path}. Téléchargez le modèle dans Paramètres.");
            await using var s = File.OpenRead(target);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(s, ct));
            if (!hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Modèle endommagé : {f.Path}. Relancez le téléchargement.");
        }

        Status = "Fichiers vérifiés (SHA-256)";
    }

    public async Task DownloadAsync(IProgress<WorkProgress> progress, CancellationToken ct)
    {
        if (!PresentIn(paths.Models) && PresentIn(BundledPath))
        {
            try
            {
                await ValidateAsync(ct);
                progress.Report(new("Modèle embarqué vérifié", Fraction: 1));
                return;
            }
            catch (InvalidDataException)
            { /* Install a repaired copy in user data without changing the bundle. */
            }
        }

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PixSeek/0.1");
        long done = 0, total = Manifest.Files.Sum(f => f.Size);
        foreach (var f in Manifest.Files)
        {
            var target = Path.Combine(paths.Models, f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                await using var existing = File.OpenRead(target);
                if (Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    done += f.Size;
                    continue;
                }
            }

            var temp = target + ".partial";
            try
            {
                using var response = await http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024];
                    long fileBytes = 0;
                    int n;
                    var last = DateTime.MinValue;
                    while ((n = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        fileBytes += n;
                        if ((DateTime.UtcNow - last).TotalMilliseconds > 100)
                        {
                            progress.Report(new($"Téléchargement {f.Path} · {MediaFormats.Size(done + fileBytes)} / {MediaFormats.Size(total)}", Fraction: (done + fileBytes) / (double)total));
                            last = DateTime.UtcNow;
                        }
                    }
                }

                await using (var check = File.OpenRead(temp))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"SHA-256 incorrect : {f.Path}");
                File.Move(temp, target, true);
                done += f.Size;
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        await ValidateAsync(ct);
        progress.Report(new("Modèle téléchargé et vérifié", Fraction: 1));
    }
}
