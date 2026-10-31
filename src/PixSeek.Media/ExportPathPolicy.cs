using PixSeek.Core;

namespace PixSeek.Media;

public static class ExportPathPolicy
{
    public static string DefaultDestination(string source)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(source));
        return Path.Combine(directory.Parent?.FullName ?? directory.FullName, directory.Name + " - " + MediaFormats.OutputFolder);
    }
    public static void EnsureIndependent(string source, string destination)
    {
        var input = ResolvePath(source); var output = ResolvePath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (output.Equals(input, comparison) || output.StartsWith(Path.TrimEndingDirectorySeparator(input) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Choisissez un dossier de sortie situé en dehors du dossier source : supprimer la source ne doit pas supprimer l'export.");
    }
    public static string ResolvePath(string path)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                current = (Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current)).ResolveLinkTarget(true)?.FullName ?? current;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
