using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

// An SDK console tool keeps the preparation step independent of MSBuild's assembly lifetime.
internal static class PrepareModels
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: PixSeek.Build <repository> <asset-list> [siglip2]");
            return 1;
        }
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            var repository = Path.GetFullPath(args[0]);
            var destination = Path.Combine(repository, "models");
            var manifests = new List<(string File, string Directory)> { ("manifest.json", "xlmr"), ("faces.json", "faces") };
            if (args.Length > 2 && args[2] == "siglip2") manifests.Add(("siglip2.json", "siglip2"));
            var files = new List<string>();
            Directory.CreateDirectory(destination);
            // Desktop and tests can build in different processes at the same time.
            using var preparationLock = AcquireLock(destination, cancellation.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PixSeek-Build/1.0");
            foreach (var manifest in manifests)
            {
                var root = Path.GetFullPath(Path.Combine(destination, manifest.Directory));
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tools", "models", manifest.File)));
                foreach (var asset in json.RootElement.GetProperty("files").EnumerateArray())
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var relativePath = asset.GetProperty("path").GetString();
                    var target = Path.GetFullPath(Path.Combine(root, relativePath));
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                        throw new InvalidDataException("Invalid model asset path: " + relativePath);
                    var size = asset.GetProperty("size").GetInt64();
                    var sha256 = asset.GetProperty("sha256").GetString();
                    if (!Valid(target, size, sha256))
                    {
                        Console.WriteLine("Downloading PixSeek model: " + manifest.Directory + "/" + relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        var partial = target + ".partial";
                        try
                        {
                            using (var response = client.GetAsync(asset.GetProperty("url").GetString(), HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult())
                            {
                                response.EnsureSuccessStatusCode();
                                using var output = File.Create(partial);
                                response.Content.CopyToAsync(output, cancellation.Token).GetAwaiter().GetResult();
                            }
                            if (!Valid(partial, size, sha256))
                                throw new InvalidDataException("Invalid model size or SHA-256: " + relativePath);
                            File.Move(partial, target, true);
                        }
                        finally { if (File.Exists(partial)) File.Delete(partial); }
                    }
                    files.Add(manifest.Directory + "/" + relativePath);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
            File.WriteAllLines(args[1], files);
            Console.WriteLine("PixSeek: model assets verified (SHA-256).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static FileStream AcquireLock(string destination, CancellationToken ct)
    {
        var path = Path.Combine(destination, ".prepare.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (ct.WaitHandle.WaitOne(200)) ct.ThrowIfCancellationRequested();
            }
        }
    }

    private static bool Valid(string path, long size, string expected)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
