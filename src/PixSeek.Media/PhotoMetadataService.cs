using System.Globalization;
using System.Text;
using System.Text.Json;
using PixSeek.Core;

namespace PixSeek.Media;

public sealed record PhotoMetadataSnapshot
{
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
    public string Profiles { get; init; } = "";
    public string? IccHash { get; init; }
    public bool Complete { get; init; }
    public string? Issue { get; init; }
    public int Orientation { get; init; } = 1;
}

/// <summary>
/// Transfers descriptive metadata only. Pixel encoding and colour conversion remain the
/// responsibility of the image encoder: replacing an ICC profile is not a colour conversion.
/// </summary>
public sealed class PhotoMetadataService(NativeTools tools, ProcessRunner runner, ImageMagickService images)
{
    private static readonly Dictionary<string, string> IptcToXmp = new(StringComparer.OrdinalIgnoreCase)
    {
        ["By-line"] = "XMP-dc:Creator", ["Byline"] = "XMP-dc:Creator", ["Caption-Abstract"] = "XMP-dc:Description",
        ["CopyrightNotice"] = "XMP-dc:Rights", ["Keywords"] = "XMP-dc:Subject", ["ObjectName"] = "XMP-dc:Title",
        ["By-lineTitle"] = "XMP-photoshop:AuthorsPosition", ["Writer-Editor"] = "XMP-photoshop:CaptionWriter",
        ["Category"] = "XMP-photoshop:Category", ["City"] = "XMP-photoshop:City",
        ["Country-PrimaryLocationName"] = "XMP-photoshop:Country", ["Credit"] = "XMP-photoshop:Credit",
        ["Headline"] = "XMP-photoshop:Headline", ["SpecialInstructions"] = "XMP-photoshop:Instructions",
        ["Source"] = "XMP-photoshop:Source", ["Province-State"] = "XMP-photoshop:State",
        ["SupplementalCategories"] = "XMP-photoshop:SupplementalCategories",
        ["OriginalTransmissionReference"] = "XMP-photoshop:TransmissionReference", ["Urgency"] = "XMP-photoshop:Urgency",
        ["Country-PrimaryLocationCode"] = "XMP-iptcCore:CountryCode", ["Sub-location"] = "XMP-iptcCore:Location"
    };

    private static readonly HashSet<string> IptcStructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationRecordVersion", "EnvelopeRecordVersion", "CodedCharacterSet", "FileFormat", "FileVersion"
    };

    private static readonly HashSet<string> DerivedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Orientation", "ImageWidth", "ImageHeight", "ExifImageWidth", "ExifImageHeight", "PixelXDimension", "PixelYDimension",
        "ThumbnailOffset", "ThumbnailLength", "PreviewImageStart", "PreviewImageLength",
        "StripOffsets", "StripByteCounts", "RowsPerStrip", "TileOffsets", "TileByteCounts", "Compression", "BitsPerSample",
        "SamplesPerPixel", "PhotometricInterpretation", "PlanarConfiguration", "YCbCrSubSampling", "YCbCrPositioning",
        "IPTCDigest", "CurrentIPTCDigest", "ImageDataHash", "ImageSize", "Megapixels",
        "ImageBitDepth", "BitDepth", "ColorType", "Filter", "Interlace", "JPEGProcess", "EncodingProcess", "ColorComponents"
    };

    public bool Available => File.Exists(tools.PathFor("exiftool"));

    public async Task<PhotoMetadataSnapshot> ReadAsync(string file, CancellationToken ct = default)
    {
        var critical = await images.CriticalMetadataAsync(file, ct);
        var profiles = critical.GetValueOrDefault("profiles", "");
        var hash = critical.GetValueOrDefault("iccHash", "");
        if (!Available)
        {
            var values = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(critical.GetValueOrDefault("captureDate"))) values["ExifIFD:DateTimeOriginal"] = critical["captureDate"];
            return new()
            {
                Values = values, Profiles = profiles, IccHash = hash, Complete = false,
                Issue = "ExifTool absent : la conservation de toutes les métadonnées ne peut pas être vérifiée."
            };
        }

        var result = await ExifToolCommand.RunAsync(tools, runner,
            ["-charset", "filename=UTF8", "-json", "-G1", "-s", "-n", "-struct", "-a", "-U", "-b", "-all", file],
            ct);
        if (result.ExitCode != 0) throw new InvalidDataException("Lecture des métadonnées impossible : " + result.StandardError.Trim());
        var snapshot = ParseExifToolJson(result.StandardOutput, profiles, hash);
        if (result.StandardError.Contains("Brotli", StringComparison.OrdinalIgnoreCase) && result.StandardError.Contains("install", StringComparison.OrdinalIgnoreCase))
            snapshot = snapshot with { Complete = false, Issue = "Le module Brotli d'ExifTool est requis pour lire ces métadonnées JXL." };
        return snapshot;
    }

    public static PhotoMetadataSnapshot ParseExifToolJson(string json, string profiles = "", string? iccHash = null)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1 ||
            document.RootElement[0].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Inventaire ExifTool invalide.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? issue = null;
        var orientation = 1;
        foreach (var p in document.RootElement[0].EnumerateObject())
        {
            if (p.Name == "SourceFile") continue;
            var text = p.Value.ValueKind is JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            if (p.Name is "ExifTool:Warning" or "ExifTool:Error") { issue = text; continue; }
            if (!values.TryAdd(p.Name, text) && IsDescriptiveTag(p.Name) && !DerivedTags.Contains(TagName(p.Name)))
                issue = "Métadonnée répétée sans identité distincte : " + p.Name;
            if (p.Name == "IFD0:Orientation" && int.TryParse(text, out var value) && value is >= 1 and <= 8) orientation = value;
        }
        return new() { Values = values, Profiles = profiles, IccHash = iccHash, Complete = issue == null, Issue = issue, Orientation = orientation };
    }

    public static string? PreservationIssue(PhotoMetadataSnapshot source, string codecId)
    {
        if (!source.Complete) return source.Issue ?? "L'inventaire des métadonnées est incomplet.";
        if (codecId.Equals("png", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var (key, value) in source.Values)
        {
            if (!key.StartsWith("IPTC:", StringComparison.OrdinalIgnoreCase) || IptcStructure.Contains(TagName(key))) continue;
            if (!IptcToXmp.TryGetValue(TagName(key), out var target)) return $"Le champ {key} ne peut pas être conservé dans ce format.";
            if (source.Values.TryGetValue(target, out var existing) && !EquivalentValue(existing, value))
                return $"Les valeurs IPTC et XMP de {target} diffèrent ; conversion ambiguë refusée.";
        }
        return null;
    }

    public async Task ApplyAsync(EncodingRequest request, CompressionPlan plan, CancellationToken ct = default)
    {
        if (request.MediaType != MediaType.Image || plan.Reversible) return;
        if (!request.Options.KeepMetadata)
        {
            if (Available)
                await ExifToolCommand.CheckedAsync(tools, runner,
                    ["-charset", "filename=UTF8", "-overwrite_original", "-EXIF:all=", "-XMP:all=", "-IPTC:all=", "-Photoshop:all=", "-Comment=", request.DestinationPath], ct);
            return;
        }

        var source = await ReadAsync(request.SourcePath, ct);
        var codec = string.IsNullOrEmpty(plan.CodecId) ? Path.GetExtension(request.DestinationPath).TrimStart('.') : plan.CodecId;
        if (PreservationIssue(source, codec) is { } issue) throw new InvalidDataException(issue);
        if (!Available) return; // Metadata-free source, checked above; ICC travels through the pixel encoder.

        var args = new List<string> { "-charset", "filename=UTF8", "-overwrite_original", "-TagsFromFile", request.SourcePath, "-EXIF", "-XMP" };
        if (codec.Equals("png", StringComparison.OrdinalIgnoreCase)) args.Add("-IPTC");
        else AddIptcMapping(args, source);

        var hasExif = source.Values.Keys.Any(k => k.StartsWith("IFD", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Exif", StringComparison.OrdinalIgnoreCase));
        var hasXmp = source.Values.Keys.Any(k => k.StartsWith("XMP", StringComparison.OrdinalIgnoreCase));
        if (plan.NormalizesOrientation)
        {
            if (hasExif) args.Add("-IFD0:Orientation#=1");
            if (source.Values.ContainsKey("XMP-tiff:Orientation")) args.Add("-XMP-tiff:Orientation#=1");
            // Keep embedded previews too; their loss is detected by the full metadata inventory.
        }
        var width = plan.Width > 0 ? plan.Width : request.Metadata.Width;
        var height = plan.Height > 0 ? plan.Height : request.Metadata.Height;
        if (hasExif)
        {
            args.AddRange([$"-ExifIFD:ExifImageWidth={width}", $"-ExifIFD:ExifImageHeight={height}"]);
            if (source.Values.ContainsKey("IFD0:ImageWidth")) args.Add($"-IFD0:ImageWidth={width}");
            if (source.Values.ContainsKey("IFD0:ImageHeight")) args.Add($"-IFD0:ImageHeight={height}");
        }
        if (hasXmp)
        {
            if (source.Values.ContainsKey("XMP-exif:ExifImageWidth")) args.Add($"-XMP-exif:ExifImageWidth={width}");
            if (source.Values.ContainsKey("XMP-exif:ExifImageHeight")) args.Add($"-XMP-exif:ExifImageHeight={height}");
            if (source.Values.ContainsKey("XMP-tiff:ImageWidth")) args.Add($"-XMP-tiff:ImageWidth={width}");
            if (source.Values.ContainsKey("XMP-tiff:ImageHeight")) args.Add($"-XMP-tiff:ImageHeight={height}");
        }
        args.Add(request.DestinationPath);
        await ExifToolCommand.CheckedAsync(tools, runner, args, ct);
    }

    private static void AddIptcMapping(List<string> args, PhotoMetadataSnapshot source)
    {
        foreach (var (key, value) in source.Values)
        {
            if (!key.StartsWith("IPTC:", StringComparison.OrdinalIgnoreCase) || !IptcToXmp.TryGetValue(TagName(key), out var target) || source.Values.ContainsKey(target)) continue;
            var list = ValueList(value);
            args.Add("-" + target + "=");
            foreach (var item in list) args.Add("-" + target + "+=" + item);
        }
    }

    public async Task VerifyAsync(EncodingRequest request, CompressionPlan plan, CancellationToken ct = default)
    {
        var result = await ReadAsync(request.DestinationPath, ct);
        if (!request.Options.KeepMetadata)
        {
            if (result.Values.Any(p => IsDescriptiveTag(p.Key) && !DerivedTags.Contains(TagName(p.Key))))
                throw new InvalidDataException("La sortie contient encore des métadonnées descriptives.");
            return;
        }
        var source = await ReadAsync(request.SourcePath, ct);
        var codec = string.IsNullOrEmpty(plan.CodecId) ? Path.GetExtension(request.DestinationPath).TrimStart('.') : plan.CodecId;
        ValidateSnapshot(source, result, codec, plan.NormalizesOrientation);
        if (!string.IsNullOrEmpty(source.IccHash) && !string.Equals(source.IccHash, result.IccHash, StringComparison.Ordinal))
            throw new InvalidDataException("Profil ICC source non conservé à l'identique.");
    }

    public static void ValidateSnapshot(PhotoMetadataSnapshot source, PhotoMetadataSnapshot result, string codecId, bool normalizedOrientation)
    {
        if (PreservationIssue(source, codecId) is { } issue) throw new InvalidDataException(issue);
        if (!result.Complete) throw new InvalidDataException(result.Issue ?? "L'inventaire des métadonnées de sortie est incomplet.");
        foreach (var (key, value) in source.Values)
        {
            if (!IsDescriptiveTag(key) || DerivedTags.Contains(TagName(key))) continue;
            var target = key;
            if (key.StartsWith("IPTC:", StringComparison.OrdinalIgnoreCase) && !codecId.Equals("png", StringComparison.OrdinalIgnoreCase))
            {
                if (IptcStructure.Contains(TagName(key))) continue;
                target = IptcToXmp[TagName(key)];
            }
            if (!result.Values.TryGetValue(target, out var after) || !EquivalentValue(value, after))
                throw new InvalidDataException($"Métadonnée non conservée : {key}.");
        }
        if (normalizedOrientation && result.Orientation != 1)
            throw new InvalidDataException("Orientation EXIF incompatible avec les pixels normalisés.");
    }

    private static bool IsDescriptiveTag(string key)
    {
        var group = key.Split(':')[0];
        return group is not ("File" or "System" or "ExifTool" or "Composite") &&
            !group.StartsWith("ICC", StringComparison.OrdinalIgnoreCase) && !group.StartsWith("Track", StringComparison.OrdinalIgnoreCase);
    }

    private static string TagName(string key) => key[(key.IndexOf(':') + 1)..];

    private static string[] ValueList(string value)
    {
        if (value.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToArray();
        }
        return [value];
    }

    private static bool EquivalentValue(string before, string after) => ValueList(before).Select(s => s.Normalize(NormalizationForm.FormC))
        .SequenceEqual(ValueList(after).Select(s => s.Normalize(NormalizationForm.FormC)), StringComparer.Ordinal);
}
