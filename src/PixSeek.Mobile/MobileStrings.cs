using System.ComponentModel;
using System.Text.Json;

namespace PixSeek.Mobile;

public sealed class MobileStrings : INotifyPropertyChanged
{
    private Dictionary<string, string> _texts = [];
    public string Language { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => _texts.GetValueOrDefault(key, key);

    public MobileStrings(string language) => SetLanguage(language);
    public void SetLanguage(string language)
    {
        Language = new[] { "en", "fr", "it", "de", "es" }.Contains(language) ? language : "en";
        var assembly = typeof(MobileStrings).Assembly;
        _texts = [];
        foreach (var code in new[] { "en", Language }.Distinct())
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith("." + code + ".json", StringComparison.Ordinal)).OrderBy(n => n.Contains(".DesktopLocalization.") ? 0 : 1))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!) _texts[pair.Key] = pair.Value;
            }
        PropertyChanged?.Invoke(this, new("Item"));
        PropertyChanged?.Invoke(this, new("Item[]"));
    }
}
