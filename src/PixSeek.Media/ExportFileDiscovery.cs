using PixSeek.Core;

namespace PixSeek.Media;

/// <summary>Compression exports must report inaccessible input instead of silently declaring an incomplete copy complete.</summary>
public static class ExportFileDiscovery
{
    private static readonly HashSet<string> AdditionalMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpe", ".jfif", ".jif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".raf", ".rw2", ".pef", ".srw", ".exr",
        ".apng", ".avifs", ".hif", ".jp2", ".j2k", ".jpf", ".jpx", ".psd", ".psb"
    };
    private static readonly HashSet<string> AdditionalVideos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".3g2", ".ogv", ".mpeg", ".mpg", ".ts", ".vob", ".mxf", ".flv", ".f4v", ".divx"
    };
    private static readonly HashSet<string> Sidecars = new(StringComparer.OrdinalIgnoreCase)
    { ".xmp", ".aae", ".srt", ".ass", ".ssa", ".vtt", ".thm", ".json", ".xml", ".mie", ".dop", ".pp3", ".on1", ".cos", ".col", ".nksc", ".icc", ".icm" };
    public static bool IsSidecar(string file) => Sidecars.Contains(Path.GetExtension(file));
    public static MediaType TypeOf(string file) => AdditionalVideos.Contains(Path.GetExtension(file)) ? MediaType.Video : MediaFormats.TypeOf(file);
    public static IEnumerable<string> Enumerate(string root, bool recursive, CancellationToken ct, IReadOnlyCollection<string> excluded)
    {
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false };
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (MediaFormats.IsMedia(file) || AdditionalMedia.Contains(Path.GetExtension(file)) || AdditionalVideos.Contains(Path.GetExtension(file)) || IsSidecar(file)) yield return file;
            }
            if (!recursive) continue;
            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            {
                if (excluded.Any(p => SameDirectory(p, child))) continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Le sous-dossier lié ne peut pas être ignoré dans un export complet : " + child);
                pending.Push(child);
            }
        }
    }
    private static bool SameDirectory(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
