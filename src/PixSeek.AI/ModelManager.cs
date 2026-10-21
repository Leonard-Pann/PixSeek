using System.Security.Cryptography;
using System.Text.Json;
using PixSeek.Core;
using PixSeek.Infrastructure;

namespace PixSeek.AI;
public sealed record ModelAsset(string Path, long Size, string Sha256, string Url);
public sealed record ModelManifest(string Id, string Revision, int Dimensions, string License, string PreprocessingVersion, IReadOnlyList<ModelAsset> Files, string Kind = "xlmr");
public sealed class ModelManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public const string DefaultId = "xlmr";
    private readonly IPlatformPaths paths;
    private string _activeId;
    public string ActiveId => _activeId;
    public IReadOnlyList<string> AvailableIds { get; } = ["xlmr", "siglip2"];
    public ModelManifest Manifest => LoadManifest(_activeId);
    public string Version => Manifest.Revision + ":" + Manifest.PreprocessingVersion;
    public string Name => _activeId == "siglip2" ? "SigLIP2 · ViT-B/16" : "XLM-RoBERTa + ViT-B/32";

    public ModelManager(IPlatformPaths paths, SettingsStore? store = null)
    {
        this.paths = paths;
        _activeId = store?.Current.ActiveModelId == "siglip2" ? "siglip2" : DefaultId;
    }

    private static readonly Dictionary<string, ModelManifest> Manifests = new();
    private static ModelManifest LoadManifest(string id)
    {
        lock (Manifests)
        {
            if (!Manifests.TryGetValue(id, out var manifest))
            {
                manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models", id == "siglip2" ? "siglip2.json" : "manifest.json")), JsonOptions)!;
                Manifests[id] = manifest;
            }

            return manifest;
        }
    }

    public ModelManager For(string id)
    {
        if (!AvailableIds.Contains(id))
            throw new ArgumentException("Unknown model.");
        var manager = new ModelManager(paths);
        manager._activeId = id;
        return manager;
    }

    public void Select(string id)
    {
        var candidate = For(id);
        if (!candidate.FilesPresent)
            throw new FileNotFoundException("Download this model first.");
        _activeId = id;
    }

    public string DirectoryPath => Path.Combine(paths.Models, _activeId);

    public void Delete()
    {
        var root = Path.GetFullPath(DirectoryPath);
        foreach (var asset in Manifest.Files)
        {
            var file = Path.GetFullPath(Path.Combine(root, asset.Path));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid model path.");
            if (File.Exists(file))
                File.Delete(file);
        }
    }

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
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PixSeek/0.1");
        long done = 0, total = Manifest.Files.Sum(f => f.Size);
        foreach (var f in Manifest.Files)
        {
            var target = Path.Combine(DirectoryPath, f.Path);
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
