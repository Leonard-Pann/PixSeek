using System.Text.Json;
using System.Text.RegularExpressions;

internal static class DebianLock
{
    public static void Create(string[] inputs, string output)
    {
        var packages = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var fields = new Dictionary<string, string>();
            void Save()
            {
                if (!fields.TryGetValue("Package", out var name)) return;
                if (!packages.TryGetValue(name, out var old) || Compare(fields["Version"], old["Version"]) > 0)
                    packages[name] = fields;
                fields = new();
            }
            foreach (var line in File.ReadLines(input))
            {
                if (line.Length == 0) { Save(); continue; }
                if (line[0] == ' ') continue;
                var colon = line.IndexOf(':');
                if (colon > 0) fields[line[..colon]] = line[(colon + 1)..].Trim();
            }
            Save();
        }
        var excluded = new HashSet<string>(["libc6", "libc-bin", "gcc-12-base", "debconf", "debconf-i18n", "ucf", "dpkg", "tar", "perl", "perl-base", "install-info", "tzdata"], StringComparer.Ordinal);
        var selected = new Dictionary<string, Dictionary<string, string>>();
        var providers = new Dictionary<string, string>();
        foreach (var package in packages.Values)
            foreach (var provided in package.GetValueOrDefault("Provides", "").Split(','))
            {
                var key = Regex.Match(provided.Trim(), @"^[a-z0-9+.-]+").Value;
                if (key.Length > 0) providers.TryAdd(key, package["Package"]);
            }
        void Add(string name)
        {
            if (excluded.Contains(name) || selected.ContainsKey(name)) return;
            if (!packages.TryGetValue(name, out var package)) throw new InvalidDataException("Missing Debian dependency: " + name);
            selected[name] = package;
            var depends = string.Join(',', new[] { package.GetValueOrDefault("Depends", ""), package.GetValueOrDefault("Pre-Depends", "") });
            foreach (var item in depends.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var alternatives = item.Split('|').Select(s => Regex.Match(s.Trim(), @"^[a-z0-9+.-]+").Value).Where(s => s.Length > 0).ToArray();
                var choice = alternatives.FirstOrDefault(n => packages.ContainsKey(n) || excluded.Contains(n));
                choice ??= alternatives.Select(n => providers.GetValueOrDefault(n)).FirstOrDefault(n => n != null);
                if (choice == null) throw new InvalidDataException("Unresolved alternatives: " + item);
                Add(choice);
            }
        }
        foreach (var name in new[] { "libvlc5", "libvlccore9", "vlc-plugin-base", "vlc-plugin-video-output" }) Add(name);
        var assets = selected.Values.OrderBy(p => p["Package"]).Select(p => new
        {
            name = p["Package"], version = p["Version"], url = "https://deb.debian.org/debian/" + p["Filename"],
            sha256 = p["SHA256"], size = long.Parse(p["Size"]), filename = Path.GetFileName(p["Filename"])
        });
        File.WriteAllText(output, JsonSerializer.Serialize(assets, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static int Compare(string a, string b)
    {
        static (int Epoch, string Version) Epoch(string text)
        {
            var colon = text.IndexOf(':'); return colon < 0 ? (0, text) : (int.Parse(text[..colon]), text[(colon + 1)..]);
        }
        var x = Epoch(a); var y = Epoch(b);
        if (x.Epoch != y.Epoch) return x.Epoch.CompareTo(y.Epoch);
        a = x.Version; b = y.Version;
        var i = 0; var j = 0;
        static int Order(char c) => c == '~' ? -1 : c == '\0' || char.IsDigit(c) ? 0 : char.IsAsciiLetter(c) ? c : c + 256;
        while (i < a.Length || j < b.Length)
        {
            while (i < a.Length && !char.IsDigit(a[i]) || j < b.Length && !char.IsDigit(b[j]))
            {
                var ca = Order(i < a.Length ? a[i] : '\0'); var cb = Order(j < b.Length ? b[j] : '\0');
                if (ca != cb) return ca.CompareTo(cb);
                if (i < a.Length) i++; if (j < b.Length) j++;
            }
            while (i < a.Length && a[i] == '0') i++;
            while (j < b.Length && b[j] == '0') j++;
            var si = i; var sj = j;
            while (i < a.Length && char.IsDigit(a[i])) i++;
            while (j < b.Length && char.IsDigit(b[j])) j++;
            if (i - si != j - sj) return (i - si).CompareTo(j - sj);
            var comparison = string.CompareOrdinal(a[si..i], b[sj..j]); if (comparison != 0) return comparison;
        }
        return 0;
    }
}
