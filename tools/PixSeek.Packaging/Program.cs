using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

if (args.Length == 0) throw new ArgumentException("extract-tar | pack | inspect | icns");
switch (args[0])
{
    case "extract-tar": Extract(args[1], args[2]); break;
    case "pack": Pack(args[1], args[2], args[3]); break;
    case "verify-package":
        using (var archive = File.OpenRead(args[1]))
        using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            var files = 0; var links = 0; var models = 0; var executables = 0;
            var names = new HashSet<string>(StringComparer.Ordinal); var header = new byte[4];
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.Name.StartsWith('/') || entry.Name.Split('/').Contains("..") || entry.Name.Contains('\\')) throw new InvalidDataException("Unsafe archive entry.");
                if (entry.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".dbg") || entry.Name.Contains(".dSYM/")) throw new InvalidDataException("Unexpected debug/Windows file.");
                if (entry.EntryType == TarEntryType.HardLink)
                {
                    if (!names.Contains(entry.LinkName)) throw new InvalidDataException("Unresolved archive hardlink.");
                    links++;
                }
                else if (entry.DataStream != null)
                {
                    files++;
                    var length = entry.DataStream.ReadAtLeast(header, 4, throwOnEndOfStream: false);
                    if (length == 4 && (header[0] == 0x7f && header[1] == 'E' || header[0] == '#' && header[1] == '!' || BitConverter.ToUInt32(header) == 0xfeedfacf))
                    {
                        if (!entry.Mode.HasFlag(UnixFileMode.UserExecute)) throw new InvalidDataException("Missing executable bit: " + entry.Name);
                        executables++;
                    }
                }
                names.Add(entry.Name);
                if (entry.Name.Contains("/models/") && entry.Name.EndsWith(".onnx")) models++;
            }
            if (models != 4 || executables == 0) throw new InvalidDataException("Expected models/executables missing.");
            Console.WriteLine(JsonSerializer.Serialize(new { Files = files, HardLinks = links, OnnxModels = models, Executables = executables, DebugSymbols = 0, Status = "Verified" }));
        }
        break;
    case "inspect":
        var result = Inspect(File.ReadAllBytes(args[1]));
        Console.WriteLine(JsonSerializer.Serialize(result)); break;
    case "inspect-tree":
        var binaries = new List<object>();
        foreach (var file in Directory.EnumerateFiles(args[1], "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);
            var magic = new byte[4];
            if (stream.Read(magic) != 4 || !(magic[0] == 0x7f && magic[1] == 'E' || BitConverter.ToUInt32(magic) == 0xfeedfacf || BinaryPrimitives.ReadUInt32BigEndian(magic) is 0xcafebabe or 0xcafebabf)) continue;
            binaries.Add(new { Path = Path.GetRelativePath(args[1], file).Replace('\\', '/'), Binary = Inspect(File.ReadAllBytes(file)) });
        }
        Console.WriteLine(JsonSerializer.Serialize(binaries)); break;
    case "debian-lock": DebianLock.Create(args.Skip(2).ToArray(), args[1]); break;
    case "self-test":
        var testRoot = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(testRoot, "source"); Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "PixSeek"), "#!/bin/sh\nprintf '%s' 'accent é'\n", new UTF8Encoding(false));
        var duplicate = RandomNumberGenerator.GetBytes(70000);
        File.WriteAllBytes(Path.Combine(sourceRoot, "a.dat"), duplicate);
        File.WriteAllBytes(Path.Combine(sourceRoot, "b.dat"), duplicate);
        var packed = Path.Combine(testRoot, "test.tar.gz"); Pack(sourceRoot, packed, "PixSeek-test");
        using (var input = File.OpenRead(packed))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            var executable = false; var hardLink = false;
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.Name == "PixSeek-test/PixSeek") executable = entry.Mode.HasFlag(UnixFileMode.UserExecute);
                if (entry.EntryType == TarEntryType.HardLink) hardLink = entry.LinkName == "PixSeek-test/a.dat";
            }
            if (!executable || !hardLink) throw new InvalidDataException("Archive permissions/deduplication test failed.");
        }
        var malicious = Path.Combine(testRoot, "traversal.tar");
        using (var file = File.Create(malicious))
        using (var tar = new TarWriter(file))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../escape.txt") { DataStream = new MemoryStream([1, 2, 3]) });
        var rejected = false;
        try { Extract(malicious, Path.Combine(testRoot, "extracted")); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected || File.Exists(Path.Combine(testRoot, "escape.txt"))) throw new InvalidDataException("Path traversal test failed.");
        Console.WriteLine("Packaging tests passed: Unix execution modes, hardlinks, path traversal protection.");
        break;
    case "stage-vlc":
        var prefix = Path.GetFullPath(args[1]); var target = Path.GetFullPath(args[2]);
        var all = Directory.GetFiles(prefix, "*", SearchOption.AllDirectories);
        var libraries = all.Where(f => Path.GetFileName(f).Contains(".so", StringComparison.Ordinal) && !f.Replace('\\', '/').Contains("/vlc/plugins/"))
            .GroupBy(Path.GetFileName, StringComparer.Ordinal).ToDictionary(g => g.Key!, g => g.First(), StringComparer.Ordinal);
        var queue = new Queue<string>(); var copied = new HashSet<string>(StringComparer.Ordinal);
        void Stage(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true); queue.Enqueue(destination);
        }
        foreach (var name in new[] { "libvlc.so.5", "libvlccore.so.9" }) Stage(libraries[name], Path.Combine(target, "lib", name));
        foreach (var file in all.Where(f => f.Replace('\\', '/').Contains("/vlc/plugins/") && f.EndsWith(".so")))
        {
            var relative = file.Replace('\\', '/').Split("/vlc/plugins/")[1];
            var category = relative.Split('/')[0];
            if (category is "gui" or "visualization" or "glconv" or "vdpau" or "vaapi") continue;
            if (category == "video_output" && Path.GetFileName(file) != "libvmem_plugin.so") continue;
            Stage(file, Path.Combine(target, "plugins", relative));
        }
        var system = new HashSet<string>(["libc.so.6", "libm.so.6", "libdl.so.2", "librt.so.1", "libpthread.so.0", "libresolv.so.2", "libutil.so.1", "ld-linux-x86-64.so.2"]);
        while (queue.TryDequeue(out var file))
        {
            var info = JsonSerializer.SerializeToElement(Inspect(File.ReadAllBytes(file)));
            if (!info.TryGetProperty("Dependencies", out var needs)) continue;
            foreach (var dependency in needs.EnumerateArray().Select(n => n.GetString()!))
            {
                if (system.Contains(dependency) || !copied.Add(dependency)) continue;
                if (!libraries.TryGetValue(dependency, out var source)) throw new InvalidDataException("Missing LibVLC dependency: " + dependency);
                Stage(source, Path.Combine(target, "lib", dependency));
            }
        }
        File.Copy(Path.Combine(target, "lib", "libvlc.so.5"), Path.Combine(target, "lib", "libvlc.so"), true);
        File.Copy(Path.Combine(target, "lib", "libvlccore.so.9"), Path.Combine(target, "lib", "libvlccore.so"), true);
        Console.WriteLine($"LibVLC staged: {copied.Count} dependency names, memory video output.");
        break;
    case "icns":
        using (var output = File.Create(args[1]))
        {
            var images = new[] { ("ic08", args[2]), ("ic09", args[3]), ("ic10", args[4]) }.Select(x => (x.Item1, Bytes: File.ReadAllBytes(x.Item2))).ToArray();
            output.Write("icns"u8);
            WriteInt(output, 8 + images.Sum(i => 8 + i.Bytes.Length));
            foreach (var image in images) { output.Write(Encoding.ASCII.GetBytes(image.Item1)); WriteInt(output, image.Bytes.Length + 8); output.Write(image.Bytes); }
        }
        break;
    default: throw new ArgumentException(args[0]);
}

static void WriteInt(Stream stream, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes); }
static void Extract(string archive, string directory)
{
    var root = Path.GetFullPath(directory);
    Directory.CreateDirectory(root);
    string Target(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!Path.IsPathFullyQualified(path) || !path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe archive path: " + name);
        return path;
    }
    using var input = File.OpenRead(archive);
    using var tar = new TarReader(input);
    var links = new Dictionary<string, string>(StringComparer.Ordinal);
    while (tar.GetNextEntry() is { } entry)
    {
        if (entry.Name is "." or "./") continue;
        var destination = Target(entry.Name.TrimEnd('/'));
        if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(destination); continue; }
        if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
        {
            var target = entry.EntryType == TarEntryType.HardLink ? Target(entry.LinkName) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(destination)!, entry.LinkName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            links[destination] = target;
        }
        else if (entry.DataStream != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var file = File.Create(destination);
            entry.DataStream.CopyTo(file);
        }
    }
    // Materialize symlinks for Windows build hosts. Final tar permissions are recreated by Pack.
    for (var pass = 0; links.Count > 0 && pass < 40; pass++)
    {
        var copied = false;
        foreach (var (destination, target) in links.ToArray())
        {
            if (!File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!string.Equals(destination, target, StringComparison.OrdinalIgnoreCase)) File.Copy(target, destination, true);
            links.Remove(destination); copied = true;
        }
        if (!copied) break;
    }
    // Some package links point to optional, uninstalled tools. Never invent their contents.
    foreach (var link in links) Console.Error.WriteLine("Unresolved optional link: " + link.Key + " -> " + link.Value);
}
static void Pack(string directory, string archive, string name)
{
    var root = Path.GetFullPath(directory);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archive))!);
    var temporary = archive + ".partial";
    using (var file = File.Create(temporary))
    using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
    using (var tar = new TarWriter(gzip))
    {
        var magic = new byte[4];
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dbg") || path.EndsWith(".debug") || relative.Contains(".dSYM/"))
                throw new InvalidDataException("Debug symbol in release: " + relative);
            using var source = File.OpenRead(path);
            var length = source.Read(magic); source.Position = 0;
            var executable = length == 4 && (magic[0] == 0x7f && magic[1] == 'E' || magic[0] == '#' && magic[1] == '!' ||
                BinaryPrimitives.ReadUInt32LittleEndian(magic) is 0xfeedfacf or 0xfeedface || BinaryPrimitives.ReadUInt32BigEndian(magic) is 0xcafebabe or 0xcafebabf);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            if (executable) mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name.TrimEnd('/') + "/" + relative)
            {
                DataStream = source, Mode = mode, Uid = 0, Gid = 0,
                ModificationTime = DateTimeOffset.FromUnixTimeSeconds(1792627200)
            };
            if (source.Length >= 65536)
            {
                var hash = Convert.ToHexString(SHA256.HashData(source)); source.Position = 0;
                if (content.TryGetValue(hash, out var original))
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, entry.Name)
                    { LinkName = original, Mode = mode, Uid = 0, Gid = 0, ModificationTime = entry.ModificationTime });
                    continue;
                }
                content[hash] = entry.Name;
            }
            tar.WriteEntry(entry);
        }
    }
    File.Move(temporary, archive, true);
    Console.WriteLine(archive);
}
static object Inspect(byte[] bytes)
{
    if (bytes.Length < 32) return new { Format = "Data" };
    var dependencies = new List<string>();
    if (bytes[0] == 0x7f && bytes[1] == 'E' && bytes[4] == 2 && bytes[5] == 1)
    {
        var ph = checked((int)BitConverter.ToUInt64(bytes, 32));
        var entrySize = BitConverter.ToUInt16(bytes, 54); var count = BitConverter.ToUInt16(bytes, 56);
        int dynamic = 0, dynamicSize = 0; ulong strings = 0;
        var segments = new List<(ulong Address, ulong Offset, ulong Size)>();
        for (var i = 0; i < count; i++)
        {
            var p = ph + i * entrySize; var type = BitConverter.ToInt32(bytes, p);
            if (type == 1) segments.Add((BitConverter.ToUInt64(bytes, p + 16), BitConverter.ToUInt64(bytes, p + 8), BitConverter.ToUInt64(bytes, p + 32)));
            if (type == 2) { dynamic = checked((int)BitConverter.ToUInt64(bytes, p + 8)); dynamicSize = checked((int)BitConverter.ToUInt64(bytes, p + 32)); }
        }
        var offsets = new List<int>();
        for (var p = dynamic; dynamic != 0 && p < dynamic + dynamicSize; p += 16)
        {
            var tag = BitConverter.ToInt64(bytes, p); if (tag == 0) break;
            if (tag == 1) offsets.Add(checked((int)BitConverter.ToUInt64(bytes, p + 8)));
            if (tag == 5) strings = BitConverter.ToUInt64(bytes, p + 8);
        }
        if (strings != 0)
        {
            var s = segments.First(s => strings >= s.Address && strings < s.Address + s.Size);
            var start = checked((int)(strings - s.Address + s.Offset));
            dependencies.AddRange(offsets.Select(o => CString(bytes, start + o)));
        }
        return new { Format = "ELF", Architecture = BitConverter.ToUInt16(bytes, 18) == 62 ? "x64" : "other", Dependencies = dependencies };
    }
    if (BitConverter.ToUInt32(bytes, 0) == 0xfeedfacf)
    {
        var architecture = BitConverter.ToUInt32(bytes, 4) == 0x100000c ? "arm64" : "x64";
        var commands = BitConverter.ToInt32(bytes, 16); var p = 32;
        for (var i = 0; i < commands; i++)
        {
            var command = BitConverter.ToUInt32(bytes, p); var size = BitConverter.ToInt32(bytes, p + 4);
            if (size < 8 || p + size > bytes.Length) throw new InvalidDataException("Malformed Mach-O load command.");
            if (command is 0xc or 0x80000018 or 0x8000001f) dependencies.Add(CString(bytes, p + BitConverter.ToInt32(bytes, p + 8)));
            p += size;
        }
        return new { Format = "MachO", Architecture = architecture, Dependencies = dependencies };
    }
    if (BinaryPrimitives.ReadUInt32BigEndian(bytes) is 0xcafebabe or 0xcafebabf) return new { Format = "MachO-Fat" };
    if (bytes[0] == '#' && bytes[1] == '!') return new { Format = "Script" };
    return new { Format = "Data" };
}
static string CString(byte[] bytes, int offset)
{
    var end = Array.IndexOf(bytes, (byte)0, offset);
    return Encoding.UTF8.GetString(bytes, offset, (end < 0 ? bytes.Length : end) - offset);
}
