using PixSeek.Core;

namespace PixSeek.Infrastructure;
public static class FileDiscovery
{
    public static IEnumerable<string> Enumerate(string root, bool recursive, CancellationToken ct = default, IReadOnlyCollection<string>? excludedDirectories = null)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Dossier indisponible : {root}");
        var queue = new Stack<string>();
        queue.Push(Path.GetFullPath(root));
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            RecurseSubdirectories = false
        };
        while (queue.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (MediaFormats.IsMedia(file))
                    yield return file;
            }

            if (recursive)
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                    if (!Path.GetFileName(sub).Equals(MediaFormats.OutputFolder, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(sub).Equals("Lumina Optimized", StringComparison.OrdinalIgnoreCase) && !(excludedDirectories?.Any(p => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)), Path.TrimEndingDirectorySeparator(sub), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) ?? false))
                        queue.Push(sub);
        }
    }
}
