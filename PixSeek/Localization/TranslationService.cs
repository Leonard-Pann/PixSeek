using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using PixSeek.Core;

namespace PixSeek.Localization;
public sealed class TranslationService : INotifyPropertyChanged
{
    public static TranslationService Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    private Dictionary<string, string> _strings = [];
    public string Language { get; private set; } = "en";

    public string this[string key] => _strings.GetValueOrDefault(key, key);
    public static string Text(string key, params object[] args) => args.Length == 0 ? Current[key] : string.Format(CultureInfo.CurrentCulture, Current[key], args);
    public void Load(string language)
    {
        if (language is not ("en" or "fr" or "de" or "it" or "es"))
            language = "en";
        var file = Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json");
        _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        Language = language;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        Changed?.Invoke();
    }
}

public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(TranslationService.Language))
    {
        Source = TranslationService.Current,
        Mode = BindingMode.OneWay,
        Converter = new TranslationConverter(key)
    };
}

public sealed class TranslationConverter(string key) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => TranslationService.Text(key);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class Choice : INotifyPropertyChanged
{
    public string Id { get; }

    private readonly string _key;
    private readonly bool _literal;
    public string Label => _literal ? _key : TranslationService.Text(_key);

    public event PropertyChangedEventHandler? PropertyChanged;
    public Choice(string id, string key, bool literal = false)
    {
        Id = id;
        _key = key;
        _literal = literal;
        TranslationService.Current.Changed += () => PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
}

public sealed class CodecLabelConverter : IValueConverter
{
    public static CodecLabelConverter Instance { get; } = new();

    public static string LabelFor(string? value) => CompressionCodecs.Normalize(value) switch
    {
        "auto" => TranslationService.Text("Auto"),
        "original" => TranslationService.Text("Retain"),
        "jxl" => "JPEG XL",
        "avif" => "AVIF",
        "heic" => "HEIC",
        "png" => "PNG",
        "av1" => "AV1",
        "hevc" => "H.265 / HEVC",
        "h264" => "H.264 / AVC",
        "vp9" => "VP9",
        _ => value ?? ""
    };
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => LabelFor(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
